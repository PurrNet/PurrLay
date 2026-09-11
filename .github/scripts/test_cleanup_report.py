import contextlib
import io
import json
import tempfile
import types
import unittest
from pathlib import Path
from unittest.mock import patch

import fly_generations as g
from test_fly_generations import FakeAdmin, FakeFly, authority, balancer, fake_tunnel, relay, status


class CleanupReportTests(unittest.TestCase):
    def setUp(self):
        self.stdout = io.StringIO()
        self.stderr = io.StringIO()
        self.capture = contextlib.ExitStack()
        self.capture.enter_context(contextlib.redirect_stdout(self.stdout))
        self.capture.enter_context(contextlib.redirect_stderr(self.stderr))

    def tearDown(self):
        self.capture.close()

    def test_every_relay_skip_has_a_decision_reason_and_honest_activity(self):
        names = ["active", "legacy", "busy", "unknown", "stopped", "no-identity", "no-endpoint", "unsuperseded"]
        machines = [relay(name, base=20000 + index * 5) for index, name in enumerate(names)]
        by_id = {machine["id"]: machine for machine in machines}
        by_id["legacy"]["config"]["metadata"]["purr_owner"] = "legacy"
        by_id["stopped"]["state"] = "stopped"
        by_id["no-endpoint"]["config"]["metadata"].pop("purr_endpoint")
        by_id["unsuperseded"]["config"]["metadata"]["purr_phase"] = "active"
        endpoint = lambda name: g.metadata(by_id[name])["purr_endpoint"]
        statuses = {
            "https://balancer": authority(endpoint("active")),
            endpoint("busy"): status("busy", canRetire=False, roomCount=7, pendingAllocations=1,
                                     transportConnections=9, pipeConnections=2, pendingOffers=3),
            endpoint("unknown"): {"instanceId": "unknown-process", "deploymentId": "unknown",
                                  "draining": True, "canRetire": False},
            endpoint("no-identity"): status("no-identity", instanceId=None),
        }
        fly, admin = FakeFly(machines), FakeAdmin(statuses)
        report = g.CleanupReport(["app"])
        g.prune_relays(fly, admin, "app", "https://balancer", report=report)

        self.assertEqual(set(report.rows), {("app", name) for name in names})
        for name in names:
            with self.subTest(machine=name):
                row = report.rows[("app", name)]
                self.assertIn(row["decision"], ("Retained", "Needs review"))
                self.assertTrue(row["reason"].strip())
                self.assertEqual(row["role"], "relay")
        self.assertRegex(report.rows[("app", "active")]["reason"].lower(), "active|rout")
        self.assertRegex(report.rows[("app", "legacy")]["reason"].lower(), "legacy|unowned|unsupported")
        self.assertRegex(report.rows[("app", "stopped")]["reason"].lower(), "stopp|state|proof")
        self.assertRegex(report.rows[("app", "no-identity")]["reason"].lower(), "identity|instance|deployment")
        self.assertRegex(report.rows[("app", "no-endpoint")]["reason"].lower(), "endpoint|address")
        self.assertRegex(report.rows[("app", "unsuperseded")]["reason"].lower(), "supersed|active|phase|drain")
        self.assertIn("7", report.rows[("app", "busy")]["activity"])
        unknown_activity = report.rows[("app", "unknown")]["activity"]
        self.assertIn("?", unknown_activity)
        self.assertNotIn("0", unknown_activity, "Missing counters cannot be presented as an idle zero")
        self.assertEqual(fly.events, [])
        self.assertEqual(admin.events, [])

    def test_dependency_blocked_balancer_chain_still_reports_every_machine(self):
        machines = [balancer("old", "draining"), balancer("dependency", "draining"),
                    balancer("active"), balancer("legacy", "draining"), balancer("stopped", "draining")]
        for index, machine in enumerate(machines):
            machine["created_at"] = str(index)
        machines[3]["config"]["metadata"]["purr_owner"] = "legacy"
        machines[4]["state"] = "stopped"
        admin = FakeAdmin({
            "private:old": {"instanceId": "old-process", "canRetire": True, "successorUrl": "private:active"},
            "private:dependency": {"instanceId": "dependency-process", "canRetire": False,
                                   "successorUrl": "private:active", "legacyDependencies": [{"url": "private:legacy"}]},
        })
        fly, report = FakeFly(machines), g.CleanupReport(["app"])
        g.prune_balancers(fly, admin, "app", fake_tunnel, report=report)

        self.assertEqual(set(report.rows), {("app", machine["id"]) for machine in machines})
        for row in report.rows.values():
            self.assertNotEqual(row["decision"], "Retired")
            self.assertTrue(row["reason"].strip())
        self.assertRegex(report.rows[("app", "old")]["reason"].lower(), "chain|block|depend|preflight")
        self.assertRegex(report.rows[("app", "dependency")]["reason"].lower(), "depend|retir|proof|successor")
        self.assertEqual(fly.events, [], "Report generation cannot bypass a blocked predecessor chain")

    def test_unavailable_authority_preserves_inventory_as_not_checked(self):
        machines = [relay("first"), relay("second", base=20005)]
        fly, report = FakeFly(machines), g.CleanupReport(["app"])
        admin = FakeAdmin({"https://balancer": g.DeploymentError("Authority unavailable")})
        with self.assertRaisesRegex(g.DeploymentError, "Authority unavailable"):
            g.prune_relays(fly, admin, "app", "https://balancer", report=report)
        self.assertEqual(set(report.rows), {("app", "first"), ("app", "second")})
        self.assertTrue(all(row["decision"] == "Not checked" for row in report.rows.values()))
        self.assertTrue(all(row["reason"] for row in report.rows.values()))
        self.assertEqual(fly.events, [])

    def test_retirement_and_failed_retirement_have_distinct_outcomes(self):
        for error in (None, g.DeploymentError("Stop could not be confirmed")):
            with self.subTest(failure=error is not None):
                machine = relay()
                endpoint = g.metadata(machine)["purr_endpoint"]
                fly = FakeFly([machine])
                admin = FakeAdmin({"https://balancer": authority(), endpoint: status()})
                report = g.CleanupReport(["app"])
                if error:
                    fly.stop_destroy = lambda *args, **kwargs: (_ for _ in ()).throw(error)
                g.prune_relays(fly, admin, "app", "https://balancer", report=report)
                row = report.rows[("app", "old")]
                self.assertEqual(row["decision"], "Needs review" if error else "Retired")
                self.assertTrue(row["reason"])
                self.assertEqual("old" in fly.items, error is not None)

    def test_machine_deleted_before_volume_failure_is_still_reported_retired(self):
        machine = balancer("old", "draining")
        fly = FakeFly([machine])
        admin = FakeAdmin({"private:old": {"instanceId": "old-process", "canRetire": True,
                                           "successorUrl": "private:active"}})
        report = g.CleanupReport(["app"])

        def delete_machine_then_fail_volume(app, candidate, *args):
            del fly.items[candidate["id"]]
            raise g.MachineRetiredError("Machine deleted; owned volume cleanup remains pending")
        fly.stop_destroy = delete_machine_then_fail_volume
        g.prune_balancers(fly, admin, "app", fake_tunnel, report=report)

        self.assertNotIn("old", fly.items)
        row = report.rows[("app", "old")]
        self.assertEqual(row["decision"], "Retired")
        self.assertRegex(row["reason"].lower(), "volume")
        self.assertRegex(row["reason"].lower(), "pending|fail|remain")

    def test_stopped_machine_with_retirement_proof_reports_actual_deletion(self):
        machine = relay(state="stopped")
        machine["config"]["metadata"]["purr_retirement"] = "old-process"
        fly, admin = FakeFly([machine]), FakeAdmin({"https://balancer": authority()})
        report = g.CleanupReport(["app"])
        g.prune_relays(fly, admin, "app", "https://balancer", report=report)
        self.assertEqual(report.rows[("app", "old")]["decision"], "Retired")
        self.assertEqual(fly.events, [("destroy", "old")])

    def test_real_stop_destroy_distinguishes_post_delete_volume_failure(self):
        machine = balancer("old", "retired")
        machine["state"] = "stopped"
        machine["config"]["metadata"].update(purr_retirement="old-process", purr_volume="vol_old")
        machine["config"]["mounts"] = [{"volume": "vol_old", "path": "/data"}]
        fly, calls = g.Fly("unused-test-token"), []

        def call(method, path, body=None):
            calls.append((method, path))
            if path.endswith("/volumes/vol_old"):
                raise g.DeploymentError("Volume API unavailable")
            return {}
        with patch.object(fly, "get", return_value=machine), patch.object(fly, "call", side_effect=call):
            with self.assertRaisesRegex(g.MachineRetiredError, "Machine deleted.*volume"):
                fly.stop_destroy("app", machine, FakeAdmin({}))
        self.assertEqual(calls, [("DELETE", "/apps/app/machines/old"), ("GET", "/apps/app/volumes/vol_old")])

    def test_reports_allowlisted_data_and_escapes_untrusted_markdown(self):
        machine = relay()
        machine["config"]["env"]["SECRET"] = "never-print-config-secret"
        machine["config"]["services"][0]["checks"] = [{"headers": [{"name": "Authorization", "values": ["never-print-header-secret"]}]}]
        machine["config"]["metadata"]["unrelated"] = "never-print-extra-metadata"
        machine["config"]["metadata"]["purr_deployment"] = "release|column\n<script>alert(1)</script>"
        raw_status = status(canRetire=False, roomCount=4, pendingOffers="never-print-status-field",
                            transportConnections=-1, pipeConnections=True, internal_key_secret="never-print-status-secret",
                            config={"credentials": "never-print-nested-status"})
        report = g.CleanupReport(["app"])
        report.track("app", [machine], "relay")
        report.record("app", machine, "Retained", "Busy | next cell\n<script>alert(2)</script>", raw_status)
        allowed = {"app", "machine", "role", "state", "phase", "deployment", "decision", "reason", "activity"}
        self.assertLessEqual(set(report.rows[("app", "old")]), allowed)
        self.assertEqual(report.rows[("app", "old")]["activity"].count("?"), 4,
                         "Missing, negative, boolean and text counters are unknown, not counts")

        with tempfile.TemporaryDirectory() as directory:
            summary = Path(directory) / "step-summary.md"
            summary.write_text("Earlier step\n", encoding="utf-8")
            artifacts = Path(directory) / "artifacts"
            with patch.dict(g.os.environ, {"GITHUB_STEP_SUMMARY": str(summary), "APP_SECRET": "never-print-env-secret"}):
                report.write(artifacts)
            markdown = (artifacts / "drain-report.md").read_text(encoding="utf-8")
            encoded = (artifacts / "drain-report.json").read_text(encoding="utf-8")
            json.loads(encoded)
            all_output = markdown + encoded + summary.read_text(encoding="utf-8") + self.stdout.getvalue() + self.stderr.getvalue()
            for forbidden in ("never-print-config-secret", "never-print-header-secret", "never-print-extra-metadata",
                              "never-print-status-secret", "never-print-status-field", "never-print-nested-status", "never-print-env-secret"):
                self.assertNotIn(forbidden, all_output)
            self.assertNotIn("<script>", markdown)
            self.assertNotIn("Busy | next cell", markdown)
            self.assertNotIn("release|column", markdown)
            self.assertNotIn("\n<script>", markdown)
            self.assertTrue(summary.read_text(encoding="utf-8").startswith("Earlier step\n"))
            self.assertIn("Retained", markdown)
            self.assertIn("app", markdown)

    def test_cleanup_writes_partial_summary_in_finally_and_marks_unvisited_apps(self):
        with tempfile.TemporaryDirectory() as directory:
            summary = Path(directory) / "summary.md"
            args = types.SimpleNamespace(app="app", domain="example", report_dir=directory)
            reports = []
            real_write = g.CleanupReport.write

            def write(report, target=None):
                reports.append(report)
                return real_write(report, target)

            def fail_first(fly, admin, app, balancer_url, report=None):
                report.track(app, [relay("visible-before-failure")], "relay")
                raise g.DeploymentError("Authority is unavailable")

            with patch.object(g, "prune_relays", side_effect=fail_first), \
                    patch.object(g, "prune_balancers") as prune_balancers, \
                    patch.object(g.CleanupReport, "write", write), \
                    patch.dict(g.os.environ, {"GITHUB_STEP_SUMMARY": str(summary)}):
                with self.assertRaisesRegex(g.DeploymentError, "Authority is unavailable"):
                    g.cleanup(FakeFly([]), FakeAdmin({}), args)
            self.assertEqual(len(reports), 1)
            report = reports[0]
            expected_apps = {"app", *(f"app-{prefix}" for _, prefix in g.REGIONS)}
            self.assertEqual(set(report.apps), expected_apps)
            remaining = expected_apps - {"app-" + g.REGIONS[0][1]}
            for app in remaining:
                self.assertRegex(report.apps[app].lower(), "not checked|not inspected|not reached|not run")
            self.assertTrue(summary.exists())
            self.assertTrue((Path(directory) / "drain-report.md").exists())
            self.assertTrue((Path(directory) / "drain-report.json").exists())
            self.assertIn("visible-before-failure", summary.read_text(encoding="utf-8"))
            prune_balancers.assert_not_called()

    def test_environment_secret_in_reason_and_allowed_metadata_is_redacted_everywhere(self):
        secret = "report-redaction-secret-73bde4"
        machine = relay()
        machine["config"]["metadata"]["purr_deployment"] = "generation-" + secret
        with tempfile.TemporaryDirectory() as directory:
            summary = Path(directory) / "step-summary.md"
            with patch.dict(g.os.environ, {"APP_SECRET": secret, "GITHUB_STEP_SUMMARY": str(summary)}):
                report = g.CleanupReport(["app"])
                report.track("app", [machine], "relay")
                report.record("app", machine, "Needs review", "Retirement failed for credential " + secret)
                report.write(directory)
            outputs = {
                "stdout": self.stdout.getvalue(),
                "stderr": self.stderr.getvalue(),
                "Markdown artifact": (Path(directory) / "drain-report.md").read_text(encoding="utf-8"),
                "JSON artifact": (Path(directory) / "drain-report.json").read_text(encoding="utf-8"),
                "step summary": summary.read_text(encoding="utf-8"),
            }
            for name, output in outputs.items():
                with self.subTest(output=name):
                    self.assertNotIn(secret, output)
            self.assertIn("[redacted]", outputs["stdout"])
            self.assertIn("[redacted]", outputs["JSON artifact"])


if __name__ == "__main__":
    unittest.main()

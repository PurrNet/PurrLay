import contextlib
import copy
import io
import unittest
from unittest.mock import call, patch

import fly_generations as g
from test_fly_generations import authority, relay, status


class MetadataRetryTests(unittest.TestCase):
    def setUp(self):
        self.output = io.StringIO()
        self.capture = contextlib.ExitStack()
        self.capture.enter_context(contextlib.redirect_stdout(self.output))
        self.capture.enter_context(contextlib.redirect_stderr(self.output))

    def tearDown(self):
        self.capture.close()

    def test_transient_429_repeats_only_the_same_assignment_without_logging_its_value(self):
        fly = g.Fly("unused-test-token")
        secret_value = "do-not-log-metadata-value-5948"
        path = "/apps/app/machines/exact-machine/metadata/purr_retirement"
        with patch.object(fly, "call", side_effect=[g.HttpError(429, path), g.HttpError(429, path), {}]) as request, \
                patch.object(g.time, "sleep") as sleep:
            fly.tag("app", "exact-machine", "purr_retirement", secret_value)
        self.assertEqual(request.call_args_list, [call("POST", path, {"value": secret_value})] * 3)
        self.assertEqual(sleep.call_args_list, [call(1), call(2)])
        self.assertIn("app/exact-machine", self.output.getvalue())
        self.assertNotIn(secret_value, self.output.getvalue())

    def test_persistent_429_has_five_attempts_and_no_sleep_after_the_last_failure(self):
        fly = g.Fly("unused-test-token")
        path = "/apps/app/machines/exact-machine/metadata/purr_phase"
        with patch.object(fly, "call", side_effect=g.HttpError(429, path)) as request, \
                patch.object(g.time, "sleep") as sleep:
            with self.assertRaises(g.HttpError) as caught:
                fly.tag("app", "exact-machine", "purr_phase", "retired")
        self.assertEqual(caught.exception.status, 429)
        self.assertEqual(request.call_args_list, [call("POST", path, {"value": "retired"})] * 5)
        self.assertEqual(sleep.call_args_list, [call(1), call(2), call(4), call(8)])

    def test_non_429_and_ambiguous_transport_failures_do_not_retry(self):
        errors = [g.HttpError(code, "metadata") for code in (400, 401, 403, 404, 409, 500, 503)]
        errors += [g.DeploymentError("Lost response; assignment outcome unknown"),
                   TimeoutError("Timed out"), OSError("Connection reset")]
        for error in errors:
            with self.subTest(error=str(error)):
                fly = g.Fly("unused-test-token")
                with patch.object(fly, "call", side_effect=error) as request, patch.object(g.time, "sleep") as sleep:
                    with self.assertRaises(type(error)):
                        fly.tag("app", "old", "purr_phase", "retired")
                request.assert_called_once()
                sleep.assert_not_called()

    def run_cleanup(self, api):
        report = g.CleanupReport(["app"])
        with patch.object(g, "request", side_effect=api.request), patch.object(g.time, "sleep") as sleep:
            g.prune_relays(g.Fly("unused-test-token"), g.Admin("secret"), "app", "https://balancer", report)
        return report.rows[("app", "old")], sleep.call_args_list

    def test_second_metadata_write_recovers_then_fresh_identity_is_checked_before_deletion(self):
        api = CleanupApi(phase_failures=2)
        row, delays = self.run_cleanup(api)
        self.assertEqual(row["decision"], "Retired")
        self.assertEqual(delays, [call(1), call(2)])
        self.assertIsNone(api.machine)
        self.assertEqual(api.retirement_writes, 1)
        self.assertEqual(api.phase_writes, 3)
        phase = max(index for index, event in enumerate(api.events) if event[1].endswith("metadata/purr_phase"))
        fresh_status = max(index for index, event in enumerate(api.events) if event[1] == "https://relay.example:20000/admin/status")
        stopped = next(index for index, event in enumerate(api.events) if event[1].endswith("/machines/old/stop"))
        deleted = next(index for index, event in enumerate(api.events) if event[0] == "DELETE")
        self.assertLess(phase, fresh_status)
        self.assertLess(fresh_status, stopped)
        self.assertLess(stopped, deleted)
        self.assertEqual(api.process_checks, [("old-process", False), ("old-process", True)])

    def test_exhausted_second_write_keeps_machine_and_reports_needs_review(self):
        api = CleanupApi(phase_failures=5)
        row, delays = self.run_cleanup(api)
        self.assertEqual(row["decision"], "Needs review")
        self.assertIn("429", row["reason"])
        self.assertEqual(delays, [call(1), call(2), call(4), call(8)])
        self.assertEqual(api.phase_writes, 5)
        self.assertIsNotNone(api.machine)
        self.assertEqual(g.metadata(api.machine)["purr_retirement"], "old-process")
        self.assertEqual(api.machine["state"], "started")
        self.assertFalse(any(event[0] == "DELETE" or event[1].endswith("/stop") for event in api.events))

    def test_process_identity_change_during_retry_prevents_stop_and_delete(self):
        api = CleanupApi(phase_failures=1, restart_during_retry=True)
        row, delays = self.run_cleanup(api)
        self.assertEqual(row["decision"], "Needs review")
        self.assertRegex(row["reason"].lower(), "process changed|identity")
        self.assertEqual(delays, [call(1)])
        self.assertEqual(api.phase_writes, 2)
        self.assertEqual(api.process_checks, [("old-process", False), ("replacement-process", True)])
        self.assertIsNotNone(api.machine)
        self.assertEqual(api.machine["state"], "started")
        self.assertFalse(any(event[0] == "DELETE" or event[1].endswith("/stop") for event in api.events))


class CleanupApi:
    """HTTP boundary fake: production Fly.tag, Admin and stop_destroy remain active."""
    def __init__(self, phase_failures, restart_during_retry=False):
        self.machine = relay()
        self.current_status = status()
        self.phase_failures = phase_failures
        self.restart_during_retry = restart_during_retry
        self.phase_writes = 0
        self.retirement_writes = 0
        self.events = []
        self.process_checks = []

    def request(self, method, url, body=None, headers=None, **kwargs):
        self.events.append((method, url, copy.deepcopy(body)))
        base = "https://api.machines.dev/v1/apps/app/machines"
        if method == "GET" and url == base:
            return [copy.deepcopy(self.machine)]
        if method == "GET" and url == base + "/old":
            return copy.deepcopy(self.machine)
        if method == "GET" and url == "https://balancer/admin/status":
            return authority()
        if method == "GET" and url == "https://relay.example:20000/admin/status":
            self.process_checks.append((self.current_status["instanceId"], self.current_status["retired"]))
            return copy.deepcopy(self.current_status)
        if method == "POST" and url == "https://relay.example:20000/admin/retire":
            assert headers["relay_instance_id"] == self.current_status["instanceId"]
            self.current_status["retired"] = True
            return copy.deepcopy(self.current_status)
        if method == "POST" and url == base + "/old/metadata/purr_retirement":
            self.retirement_writes += 1
            self.machine["config"]["metadata"]["purr_retirement"] = body["value"]
            return {}
        if method == "POST" and url == base + "/old/metadata/purr_phase":
            self.phase_writes += 1
            if self.phase_writes <= self.phase_failures:
                if self.restart_during_retry:
                    # Keep every other retirement field valid to isolate the identity guard.
                    self.current_status["instanceId"] = "replacement-process"
                raise g.HttpError(429, url)
            self.machine["config"]["metadata"]["purr_phase"] = body["value"]
            return {}
        if method == "POST" and url == base + "/old/stop":
            self.machine["state"] = "stopped"
            return {}
        if method == "DELETE" and url == base + "/old":
            assert self.machine["state"] == "stopped"
            self.machine = None
            return {}
        raise AssertionError(f"Unexpected request: {method} {url}")


if __name__ == "__main__":
    unittest.main()

import contextlib
import copy
import io
import socket
import subprocess
import sys
import types
import unittest
from unittest.mock import patch

import fly_generations as g


def relay(machine_id="old", phase="draining", state="started", base=20000):
    config = g.relay_config("image", machine_id, "relay.example", "france", "https://balancer",
                            "192.0.2.1", base, "secret")
    config["metadata"]["purr_phase"] = phase
    return {"id": machine_id, "name": f"purr-relay-{machine_id}", "state": state, "config": config}


def balancer(machine_id, phase="active"):
    config = g.balancer_config("image", machine_id, None, "secret")
    config["metadata"]["purr_phase"] = phase
    return {"id": machine_id, "state": "started", "config": config}


class FakeFly:
    def __init__(self, machines):
        self.items = {m["id"]: copy.deepcopy(m) for m in machines}
        self.events = []

    def machines(self, app):
        return copy.deepcopy(list(self.items.values()))

    def get(self, app, machine_id):
        return copy.deepcopy(self.items[machine_id])

    def tag(self, app, machine_id, key, value):
        self.events.append(("tag", machine_id, key, value))
        self.items[machine_id]["config"]["metadata"][key] = value

    def action(self, app, machine_id, action, body=None):
        self.events.append((action, machine_id))
        if action in ("start", "stop"):
            self.items[machine_id]["state"] = "started" if action == "start" else "stopped"

    def create(self, app, deployment, role, region, config, cordoned=False):
        self.events.append(("create", deployment, cordoned))
        machine = {"id": deployment, "state": "started", "config": copy.deepcopy(config)}
        self.items[deployment] = machine
        return copy.deepcopy(machine)

    def ensure_volume(self, app, deployment, region):
        self.events.append(("volume", deployment))
        return {"id": "vol_" + deployment}

    def stop_destroy(self, app, machine, admin, open_tunnel=None):
        self.events.append(("destroy", machine["id"]))
        del self.items[machine["id"]]


class FakeAdmin:
    headers = {"internal_key_secret": "secret"}

    def __init__(self, statuses, mutations=None):
        self.statuses = statuses
        self.mutations = mutations or {}
        self.events = []
        self.hosts = []

    def status(self, endpoint, host=None):
        self.hosts.append((endpoint, host))
        result = self.statuses[endpoint]
        if isinstance(result, Exception):
            raise result
        return copy.deepcopy(result)

    def mutate(self, endpoint, operation, instance):
        self.events.append((operation, endpoint, instance))
        result = self.mutations.get(operation)
        if isinstance(result, Exception):
            raise result
        if result is not None:
            return copy.deepcopy(result)
        status = self.status(endpoint)
        if operation == "activate":
            status["draining"] = False
        elif operation == "drain":
            status["draining"] = True
        elif operation == "retire":
            status["retired"] = True
        return status

    def call(self, endpoint, path, body=None, method="GET"):
        self.events.append((path, endpoint, body))
        result = self.mutations.get(path)
        if isinstance(result, Exception):
            raise result
        return result


def status(deployment="old", **overrides):
    return {"instanceId": deployment + "-process", "deploymentId": deployment,
            "ready": True, "draining": True, "retired": False, "canRetire": True,
            "roomCount": 0, **overrides}


def authority(*active):
    return {"ready": True, "successorUrl": None,
            "relayServers": [{"apiEndpoint": endpoint, "draining": False} for endpoint in active]}


def once(probe, description, **kwargs):
    result = probe()
    if not result:
        raise g.DeploymentError("Not ready: " + description)
    return result


@contextlib.contextmanager
def fake_tunnel(app, machine_id):
    yield "private:" + machine_id


class HttpDiagnosticsTests(unittest.TestCase):
    url = "https://api.machines.dev/v1/apps/app/machines"

    def fail_request(self, response, body=None, headers=None, url=None):
        raw = response if isinstance(response, bytes) else g.json.dumps(response).encode()
        stream = io.BytesIO(raw)
        error = g.urllib.error.HTTPError(url or self.url, 400, "Bad Request", {}, stream)
        with patch.object(g.urllib.request, "build_opener") as opener:
            opener.return_value.open.side_effect = error
            with self.assertRaises(g.HttpError) as caught:
                g.request("POST", url or self.url, body, headers)
        self.assertEqual(caught.exception.status, 400)
        self.assertTrue(stream.closed)
        return str(caught.exception)

    def test_machines_validation_reason_is_visible(self):
        for response in ({"error": "region hkg is unavailable"},
                         {"message": "region hkg is unavailable"},
                         {"error": {"message": "region hkg is unavailable"}}):
            with self.subTest(response=response):
                self.assertEqual(self.fail_request(response), f"HTTP 400: {self.url}: region hkg is unavailable")

    def test_error_details_redact_credentials_and_omit_embedded_request_payloads(self):
        token, secret, environment_secret = "FlyV1 a-private-token", "app-secret-value", "environment-secret-value"
        body = {"config": {"env": {"SECRET": secret}, "services": [{"checks": [{"headers": [
            {"name": "internal_key_secret", "values": ["check-header-secret"]}]}]}]}}
        message = ("region hkg unavailable; " + token + " " + g.urllib.parse.quote(token, safe="") + " " + secret +
                   " " + environment_secret + " check-header-secret; request={\"unrelated\":\"private-payload\"}")
        with patch.dict(g.os.environ, {"APP_SECRET": environment_secret}, clear=True):
            diagnostic = self.fail_request({"error": message, "request": body}, body,
                                           {"Authorization": "Bearer " + token})
        self.assertIn("region hkg unavailable", diagnostic)
        for value in (token, "a-private-token", secret, environment_secret, "check-header-secret", "private-payload"):
            self.assertNotIn(value, diagnostic)
        self.assertNotIn("{", diagnostic)

    def test_unrecognized_bearer_or_credential_assignment_is_redacted(self):
        diagnostic = self.fail_request({"error": "invalid region; Bearer unrecognized-token; password=hidden-value"})
        self.assertNotIn("unrecognized-token", diagnostic)
        self.assertNotIn("hidden-value", diagnostic)
        self.assertIn("invalid region", diagnostic)

    def test_non_json_oversized_or_structured_errors_keep_safe_status_only(self):
        for response in (b"<html>private upstream details</html>", b"x" * 9000,
                         {"error": ["private details"]}, ["private details"]):
            with self.subTest(response_type=type(response).__name__):
                self.assertEqual(self.fail_request(response), f"HTTP 400: {self.url}")

    def test_details_are_single_line_bounded_and_machines_api_only(self):
        diagnostic = self.fail_request({"error": "region unavailable\n\r\t" + "x" * 1000})
        self.assertNotIn("\n", diagnostic)
        self.assertNotIn("\r", diagnostic)
        self.assertNotIn("\t", diagnostic)
        self.assertEqual(len(diagnostic.removeprefix(f"HTTP 400: {self.url}: ")), 512)
        other_url = "https://relay.example/admin/offer"
        self.assertEqual(self.fail_request({"error": "private application details"}, url=other_url),
                         f"HTTP 400: {other_url}")

    def test_error_body_read_is_bounded(self):
        class RecordingStream(io.BytesIO):
            def read(self, size=-1):
                self.requested_size = size
                return super().read(size)
        stream = RecordingStream(b"x" * 10000)
        error = g.urllib.error.HTTPError(self.url, 400, "Bad Request", {}, stream)
        self.assertIsNone(g.machine_api_error_detail(error, None, None))
        self.assertEqual(stream.requested_size, 8193)
        self.assertTrue(stream.closed)


class ProxyTests(unittest.TestCase):
    child_code = """
import socket, sys, threading
if sys.argv[2] == 'exit':
    sys.exit(23)
if sys.argv[2] == 'listen':
    listener = socket.socket()
    listener.bind(('127.0.0.1', int(sys.argv[1])))
    listener.listen()
    def accept():
        while True:
            connection, _ = listener.accept()
            connection.close()
    threading.Thread(target=accept, daemon=True).start()
sys.stdin.buffer.read()
"""

    def setUp(self):
        self.processes = []
        self.real_popen = subprocess.Popen
        self.stderr = contextlib.redirect_stderr(io.StringIO())
        self.stderr.__enter__()

    def tearDown(self):
        self.stderr.__exit__(None, None, None)
        for process in self.processes:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=3)

    def launch(self, command, mode, **kwargs):
        self.assertEqual(command[0:2], ["flyctl", "proxy"])
        self.assertEqual(command[3], "exact-machine.vm.app.internal")
        self.assertEqual(command[4:], ["--app", "app", "--bind-addr", "127.0.0.1", "--watch-stdin"])
        local_port, remote_port = command[2].split(":")
        self.assertEqual(remote_port, "8080")
        process = self.real_popen([sys.executable, "-c", self.child_code, local_port, mode], **kwargs)
        self.processes.append(process)
        return process

    def assert_reaped(self):
        self.assertTrue(self.processes)
        self.assertTrue(all(process.poll() is not None for process in self.processes))
        self.assertTrue(all(process.stdin.closed for process in self.processes))

    def test_exited_proxy_is_relaunched_and_real_socket_becomes_available(self):
        def launch(command, **kwargs):
            return self.launch(command, "exit" if not self.processes else "listen", **kwargs)
        with patch.object(g.subprocess, "Popen", side_effect=launch):
            with g.tunnel("app", "exact-machine", startup_timeout=3, retry_interval=0.01) as endpoint:
                port = int(endpoint.rsplit(":", 1)[1])
                with socket.create_connection(("127.0.0.1", port), timeout=1):
                    pass
                self.assertEqual(len(self.processes), 2)
                self.assertEqual(self.processes[0].returncode, 23)
        self.assert_reaped()

    def test_repeated_proxy_exits_exhaust_one_deadline_without_orphans(self):
        elapsed = 0
        def sleep(seconds):
            nonlocal elapsed
            elapsed += seconds
        def exited(command, **kwargs):
            process = self.launch(command, "exit", **kwargs)
            process.wait(timeout=3)
            return process
        with patch.object(g.subprocess, "Popen", side_effect=exited):
            with self.assertRaisesRegex(g.DeploymentError, "Timed out starting private proxy"):
                with g.tunnel("app", "exact-machine", startup_timeout=1.2, retry_interval=0.2,
                              clock=lambda: elapsed, sleep=sleep):
                    self.fail("An exited proxy cannot provide a tunnel")
        self.assertAlmostEqual(elapsed, 1.2)
        self.assertEqual(len(self.processes), 3)
        self.assert_reaped()

    def test_live_proxy_that_never_listens_is_stopped_at_deadline(self):
        with patch.object(g.subprocess, "Popen", side_effect=lambda command, **kwargs:
                          self.launch(command, "wait", **kwargs)):
            with self.assertRaisesRegex(g.DeploymentError, "Timed out starting private proxy"):
                with g.tunnel("app", "exact-machine", startup_timeout=0.3, retry_interval=0.01):
                    self.fail("A child without a listening socket cannot provide a tunnel")
        self.assert_reaped()

    def test_caller_failure_closes_proxy_without_replaying_caller(self):
        with patch.object(g.subprocess, "Popen", side_effect=lambda command, **kwargs:
                          self.launch(command, "listen", **kwargs)):
            with self.assertRaisesRegex(RuntimeError, "caller failed"):
                with g.tunnel("app", "exact-machine", startup_timeout=3, retry_interval=0.01):
                    raise RuntimeError("caller failed")
        self.assertEqual(len(self.processes), 1)
        self.assert_reaped()


class PortTests(unittest.TestCase):
    def test_reserves_stopped_machines_and_ranges_across_protocols(self):
        machines = [relay(state="stopped")]
        machines.append({"config": {"services": [{"protocol": "udp", "ports": [
            {"start_port": 20007, "end_port": 20011}]}]}})
        original = copy.deepcopy(machines)
        self.assertEqual(g.select_port_block(machines), 20015)
        self.assertEqual(machines, original)

    def test_partial_block_collision_and_exhaustion(self):
        machines = [{"config": {"services": [{"ports": [{"port": 20004}]}]}}]
        self.assertEqual(g.select_port_block(machines), 20005)
        machines[0]["config"]["services"][0]["ports"] = [{"start_port": 20000, "end_port": 59999}]
        with self.assertRaises(g.DeploymentError):
            g.select_port_block(machines)

    def test_unknown_port_shape_fails_closed(self):
        with self.assertRaises(g.DeploymentError):
            g.select_port_block([{"config": {"services": [{"ports": [{}]}]}}])
        with self.assertRaises(g.DeploymentError):
            g.select_port_block([{"id": "unreachable", "incomplete_config": {}}])

    def test_relay_ports_preserve_cached_hostname_and_udp_port_numbers(self):
        config = relay()["config"]
        self.assertEqual(config["env"]["HOST_DOMAIN"], "relay.example")
        self.assertEqual(config["env"]["HOST_ENDPOINT"], "https://relay.example:20000")
        self.assertEqual(config["env"]["RELAY_START_STANDBY"], "true")
        self.assertEqual(config["services"][0]["internal_port"], 8081)
        self.assertEqual(config["services"][0]["ports"][0]["handlers"], ["tls", "http"])
        for service in config["services"][1:]:
            self.assertEqual(service["internal_port"], service["ports"][0]["port"])
        self.assertNotIn("fly_platform_version", config["metadata"])
        self.assertTrue(all(s["autostop"] == "off" for s in config["services"]))

    def test_balancer_has_service_readiness_not_only_observability_check(self):
        config = balancer("new")["config"]
        self.assertEqual(config["services"][0]["checks"][0]["path"], "/admin/ready")


class PruneTests(unittest.TestCase):
    def setUp(self):
        self.machine = relay()
        self.endpoint = g.metadata(self.machine)["purr_endpoint"]
        self.fly = FakeFly([self.machine])
        self.admin = FakeAdmin({"https://balancer": authority(), self.endpoint: status()})
        self.stderr = contextlib.redirect_stderr(io.StringIO())
        self.stderr.__enter__()

    def tearDown(self):
        self.stderr.__exit__(None, None, None)

    def prune(self):
        g.prune_relays(self.fly, self.admin, "app", "https://balancer")

    def test_active_endpoint_excluded_even_with_stale_draining_metadata(self):
        self.admin.statuses["https://balancer"] = authority(self.endpoint)
        self.prune()
        self.assertEqual(self.fly.events, [])
        self.assertEqual(self.admin.events, [])

    def test_unknown_authority_prevents_all_retirement(self):
        self.admin.statuses["https://balancer"] = {"ready": False}
        with self.assertRaises(g.DeploymentError):
            self.prune()
        self.assertEqual(self.fly.events, [])

    def test_nonempty_unknown_restarted_or_undrained_relay_is_retained(self):
        cases = [status(canRetire=False), status(draining=False), status(deployment="other"),
                 g.HttpError(404, self.endpoint), g.DeploymentError("offline")]
        for candidate in cases:
            with self.subTest(candidate=candidate):
                self.admin.statuses[self.endpoint] = candidate
                self.prune()
                self.assertEqual(self.fly.events, [])
                self.assertEqual(self.admin.events, [])

    def test_lost_retire_response_or_changed_instance_never_destroys(self):
        for result in (g.DeploymentError("lost response"), g.HttpError(409, self.endpoint),
                       status(retired=True, instanceId="different"), status(retired=False)):
            with self.subTest(result=result):
                self.admin.mutations["retire"] = result
                self.prune()
                self.assertEqual(self.fly.events, [])

    def test_atomic_retirement_precedes_durable_proof_and_destroy(self):
        self.prune()
        self.assertEqual(self.admin.events, [("retire", self.endpoint, "old-process")])
        self.assertEqual(self.fly.events[0], ("tag", "old", "purr_retirement", "old-process"))
        self.assertEqual(self.fly.events[-1], ("destroy", "old"))

    def test_cleanup_recovers_after_central_activation_before_old_drain(self):
        self.fly.items["old"]["config"]["metadata"]["purr_phase"] = "active"
        self.admin.statuses["https://balancer"]["relayServers"] = [
            {"apiEndpoint": self.endpoint, "deploymentId": "old", "draining": True}]
        self.admin.statuses[self.endpoint]["draining"] = False
        self.admin.mutations["retire"] = status(retired=True)
        self.prune()
        self.assertEqual([event[0] for event in self.admin.events], ["drain", "retire"])
        self.assertEqual(self.fly.events[-1], ("destroy", "old"))

    def test_missing_registry_is_not_proof_active_tagged_relay_was_superseded(self):
        self.fly.items["old"]["config"]["metadata"]["purr_phase"] = "active"
        self.prune()
        self.assertEqual(self.fly.events, [])
        self.assertEqual(self.admin.events, [])

    def test_stopped_machine_without_proof_is_preserved(self):
        self.fly.items["old"]["state"] = "stopped"
        self.prune()
        self.assertEqual(self.fly.events, [])

    def test_cleanup_resumes_after_acknowledged_retirement_and_stop(self):
        self.fly.items["old"]["state"] = "stopped"
        self.fly.items["old"]["config"]["metadata"]["purr_retirement"] = "old-process"
        self.prune()
        self.assertEqual(self.fly.events, [("destroy", "old")])

    def test_legacy_and_active_generations_are_not_candidates(self):
        for change in ({"purr_owner": "legacy"}, {"purr_phase": "active"}, {"purr_phase": "standby"}):
            self.fly.items["old"] = copy.deepcopy(self.machine)
            self.fly.items["old"]["config"]["metadata"].update(change)
            self.prune()
            self.assertEqual(self.fly.events, [])

    def test_balancer_dependency_requires_explicit_retirement_proof(self):
        machine = balancer("old", "draining")
        fly = FakeFly([machine])
        admin = FakeAdmin({"private:old": {"instanceId": "old-process", "successorUrl": "new",
                                             "canRetire": False}})
        g.prune_balancers(fly, admin, "app", fake_tunnel)
        self.assertEqual(fly.events, [])
        admin.statuses["private:old"]["canRetire"] = True
        g.prune_balancers(fly, admin, "app", fake_tunnel)
        self.assertEqual(fly.events[-1], ("destroy", "old"))


class ManagedPredecessorTests(unittest.TestCase):
    def setUp(self):
        self.legacy = balancer("legacy")
        self.legacy["config"]["metadata"] = {}
        self.opened = []
        self.output = io.StringIO()
        self.capture = contextlib.redirect_stdout(self.output)
        self.capture.__enter__()

    def tearDown(self):
        self.capture.__exit__(None, None, None)

    @contextlib.contextmanager
    def managed_tunnel(self, app, machine_id):
        self.assertEqual(app, "app")
        self.assertNotEqual(machine_id, "legacy", "Managed selection must never probe the IPv4-only legacy balancer")
        self.opened.append(machine_id)
        yield "private:" + machine_id

    def assert_unchanged(self, fly, original, admin):
        self.assertEqual(fly.items, original)
        self.assertEqual(fly.events, [])
        self.assertEqual(admin.events, [])

    def test_healthy_managed_leader_ignores_ipv4_only_legacy_in_any_inventory_order(self):
        leader = balancer("leader")
        for machines in ([self.legacy, leader], [leader, self.legacy]):
            with self.subTest(first=machines[0]["id"]):
                self.opened.clear()
                fly = FakeFly(machines)
                original = copy.deepcopy(fly.items)
                admin = FakeAdmin({"private:leader": {"ready": True, "successorUrl": None},
                                   "private:legacy": OSError("IPv4-only legacy connection reset")})
                selected = g.find_predecessor(fly, admin, "app", open_tunnel=self.managed_tunnel)
                self.assertEqual(selected["id"], "leader")
                self.assertEqual(self.opened, ["leader"])
                self.assertEqual(admin.hosts, [("private:leader", "leader.vm.app.internal:8080")])
                self.assert_unchanged(fly, original, admin)

    def test_forwarding_managed_generation_is_not_selected_over_live_authority(self):
        fly = FakeFly([self.legacy, balancer("forwarder", "draining"), balancer("leader")])
        original = copy.deepcopy(fly.items)
        admin = FakeAdmin({"private:forwarder": {"ready": True, "successorUrl": "private:leader"},
                           "private:leader": {"ready": True}})
        selected = g.find_predecessor(fly, admin, "app", open_tunnel=self.managed_tunnel)
        self.assertEqual(selected["id"], "leader")
        self.assertEqual(self.opened, ["forwarder", "leader"])
        self.assert_unchanged(fly, original, admin)

    def test_unreachable_managed_generation_cannot_fall_back_to_healthy_legacy(self):
        for error in (g.DeploymentError("private endpoint unavailable"), OSError("connection reset"),
                      g.HttpError(404, "private:managed")):
            with self.subTest(error=type(error).__name__):
                self.opened.clear()
                fly = FakeFly([self.legacy, balancer("managed")])
                original = copy.deepcopy(fly.items)
                admin = FakeAdmin({"private:managed": error, "private:legacy": {"ready": True}})
                with self.assertRaises(g.DeploymentError):
                    g.find_predecessor(fly, admin, "app", open_tunnel=self.managed_tunnel)
                self.assertEqual(self.opened, ["managed"])
                self.assert_unchanged(fly, original, admin)

    def test_stopped_managed_generation_prevents_legacy_fallback(self):
        stopped = balancer("managed")
        stopped["state"] = "stopped"
        fly = FakeFly([self.legacy, stopped])
        original = copy.deepcopy(fly.items)
        admin = FakeAdmin({"private:legacy": {"ready": True}})
        with self.assertRaises(g.DeploymentError):
            g.find_predecessor(fly, admin, "app", open_tunnel=self.managed_tunnel)
        self.assertEqual(self.opened, [])
        self.assertEqual(admin.hosts, [])
        self.assert_unchanged(fly, original, admin)

    def test_two_managed_authorities_fail_without_consulting_legacy(self):
        fly = FakeFly([self.legacy, balancer("one"), balancer("two")])
        original = copy.deepcopy(fly.items)
        admin = FakeAdmin({"private:one": {"ready": True}, "private:two": {"ready": True},
                           "private:legacy": {"ready": True}})
        with self.assertRaises(g.DeploymentError):
            g.find_predecessor(fly, admin, "app", open_tunnel=self.managed_tunnel)
        self.assertEqual(self.opened, ["one", "two"])
        self.assert_unchanged(fly, original, admin)

    def test_no_managed_authority_fails_without_consulting_legacy(self):
        for candidate in ({"ready": False}, {"ready": True, "successorUrl": "private:missing-successor"}):
            with self.subTest(status=candidate):
                self.opened.clear()
                fly = FakeFly([self.legacy, balancer("managed")])
                original = copy.deepcopy(fly.items)
                admin = FakeAdmin({"private:managed": candidate, "private:legacy": {"ready": True}})
                with self.assertRaises(g.DeploymentError):
                    g.find_predecessor(fly, admin, "app", open_tunnel=self.managed_tunnel)
                self.assertEqual(self.opened, ["managed"])
                self.assert_unchanged(fly, original, admin)


class DeploymentTests(unittest.TestCase):
    def relay_args(self):
        return types.SimpleNamespace(app="app", deployment="new", image="image", region="cdg",
                                     domain="relay.example", prefix="france", balancer="https://balancer",
                                     public_ip="192.0.2.1", secret="secret")

    @patch.object(g, "wait_for", once)
    def test_failed_replacement_does_not_drain_or_destroy_previous_relay(self):
        fly = FakeFly([relay()])
        admin = FakeAdmin({"https://relay.example:20005": status("new", ready=False)})
        with self.assertRaises(g.DeploymentError):
            g.deploy_relay(fly, admin, self.relay_args())
        self.assertEqual(admin.events, [])
        self.assertEqual(fly.events, [("create", "new", False)])

    @patch.object(g, "wait_for", once)
    def test_failed_balancer_activation_does_not_drain_previous_relay(self):
        fly = FakeFly([relay()])
        admin = FakeAdmin({"https://relay.example:20005": status("new")},
                          {"/admin/activateRelay": g.HttpError(409, "https://balancer")})
        with self.assertRaises(g.DeploymentError):
            g.deploy_relay(fly, admin, self.relay_args())
        self.assertFalse(any(event[0] == "drain" for event in admin.events))
        self.assertFalse(any(event[0] == "destroy" for event in fly.events))

    @patch.object(g, "wait_for", once)
    def test_activation_is_acknowledged_before_draining_previous_relay(self):
        fly = FakeFly([relay()])
        new_endpoint = "https://relay.example:20005"
        admin = FakeAdmin({new_endpoint: status("new"), "https://relay.example:20000": status(),
                           "https://balancer": authority(new_endpoint)})
        g.deploy_relay(fly, admin, self.relay_args())
        self.assertEqual([event[0] for event in admin.events], ["activate", "/admin/activateRelay", "drain"])
        self.assertEqual(g.metadata(fly.items["old"])["purr_phase"], "draining")
        self.assertNotIn(("destroy", "old"), fly.events)

    @patch.object(g, "wait_for", once)
    def test_failed_handoff_never_cordons_predecessor(self):
        old = balancer("old")
        fly = FakeFly([old])
        admin = FakeAdmin({"private:old": {"ready": True, "successorUrl": None},
                           "private:new": status("new", ready=False)})
        args = types.SimpleNamespace(app="app", deployment="new", image="image", region="cdg",
                                     secret="secret", public_url="https://balancer")
        with self.assertRaises(g.DeploymentError):
            g.deploy_balancer(fly, admin, args, fake_tunnel)
        self.assertEqual(fly.events, [("volume", "new"), ("create", "new", True)])
        self.assertEqual(fly.items["new"]["config"]["mounts"], [{"volume": "vol_new", "path": "/data"}])

    @patch.object(g, "wait_for", once)
    @patch.object(g, "request", return_value={"ready": True, "instanceId": "new-process"})
    def test_new_balancer_is_public_before_predecessor_cordon(self, _request):
        old = balancer("old")
        fly = FakeFly([old])
        admin = FakeAdmin({"private:old": {"ready": True, "successorUrl": None},
                           "private:new": status("new", legacyDependencies=[])})
        args = types.SimpleNamespace(app="app", deployment="new", image="image", region="cdg",
                                     secret="secret", public_url="https://balancer")
        g.deploy_balancer(fly, admin, args, fake_tunnel)
        self.assertLess(fly.events.index(("uncordon", "new")), fly.events.index(("cordon", "old")))
        self.assertNotIn(("destroy", "old"), fly.events)
        self.assertEqual(admin.hosts, [("private:old", "old.vm.app.internal:8080"),
                                      ("private:new", "new.vm.app.internal:8080")])
        self.assertNotIn("Host", _request.call_args.kwargs["headers"])

    @patch.object(g, "request", return_value={"ready": True})
    def test_private_host_header_is_scoped_to_tunnel_request(self, request):
        admin = g.Admin("secret")
        admin.status("http://127.0.0.1:45678", host=g.private_host("app", "exact-machine"))
        request.assert_called_once_with("GET", "http://127.0.0.1:45678/admin/status", None,
                                        {"internal_key_secret": "secret", "Host": "exact-machine.vm.app.internal:8080"})
        request.reset_mock()
        admin.status("https://balancer")
        request.assert_called_once_with("GET", "https://balancer/admin/status", None,
                                        {"internal_key_secret": "secret"})

    @patch.object(g, "wait_for", once)
    @patch.object(g, "request", return_value={"ready": True, "instanceId": "new-process"})
    def test_retry_finishes_predecessor_finalization_before_marking_successor_active(self, _request):
        for legacy in (False, True):
            failures = [("cordon", "old")]
            if not legacy:
                failures.append(("tag", "old", "purr_phase", "draining"))
            for failure in failures:
                for lost_response in (False, True):
                    with self.subTest(legacy=legacy, failure=failure, lost_response=lost_response):
                        old = balancer("old")
                        if legacy:
                            old["config"]["metadata"] = {}
                        fly = FakeFly([old])
                        admin = FakeAdmin({
                            "private:old": g.HttpError(404, "private:old") if legacy else authority(),
                            "private:new": status("new", legacyDependencies=[]),
                        })
                        args = types.SimpleNamespace(app="app", deployment="new", image="image",
                                                     region="cdg", secret="secret", public_url="https://balancer")
                        original_action, original_tag = fly.action, fly.tag

                        def inject(event, operation, *arguments):
                            if event == failure and not lost_response:
                                raise g.DeploymentError("injected finalization failure")
                            operation(*arguments)
                            if event == failure:
                                raise g.DeploymentError("injected lost finalization response")

                        with patch.object(fly, "action", side_effect=lambda app, mid, action:
                                          inject((action, mid), original_action, app, mid, action)), \
                                patch.object(fly, "tag", side_effect=lambda app, mid, key, value:
                                             inject(("tag", mid, key, value), original_tag, app, mid, key, value)):
                            with self.assertRaises(g.DeploymentError):
                                g.deploy_balancer(fly, admin, args, fake_tunnel)

                        self.assertEqual(g.metadata(fly.items["new"])["purr_phase"], "standby")
                        self.assertNotIn(("tag", "new", "purr_phase", "active"), fly.events)
                        args.deployment, args.image = "retry", "next-image"
                        g.resume_pending_balancer(fly, admin, args, fake_tunnel)

                        self.assertEqual(set(fly.items), {"old", "new"})
                        self.assertEqual(fly.items["new"]["config"]["image"], "image")
                        self.assertEqual(fly.items["new"]["config"]["mounts"],
                                         [{"volume": "vol_new", "path": "/data"}])
                        self.assertEqual(fly.events.count(("create", "new", True)), 1)
                        self.assertEqual(fly.events.count(("volume", "new")), 1)
                        self.assertEqual(fly.events[-1], ("tag", "new", "purr_phase", "active"))
                        self.assertIn(("cordon", "old"), fly.events)
                        if legacy:
                            self.assertEqual(g.metadata(fly.items["old"]), {})
                        else:
                            self.assertEqual(fly.events[-2], ("tag", "old", "purr_phase", "draining"))
                        self.assertFalse(any(event[0] == "destroy" for event in fly.events))

    def test_ambiguous_predecessors_block_deploy(self):
        fly = FakeFly([balancer("one"), balancer("two")])
        admin = FakeAdmin({"private:one": {"ready": True}, "private:two": {"ready": True}})
        with self.assertRaises(g.DeploymentError):
            g.find_predecessor(fly, admin, "app", open_tunnel=fake_tunnel)

    def test_unreachable_legacy_predecessor_reports_ipv6_prerequisite_before_create(self):
        old = balancer("old")
        old["config"]["metadata"] = {}
        args = types.SimpleNamespace(app="app", deployment="new", image="image", region="cdg",
                                     secret="secret", public_url="https://balancer")
        for error in (g.DeploymentError("connection reset"), OSError("proxy unavailable")):
            with self.subTest(error=error):
                fly = FakeFly([old])
                admin = FakeAdmin({"private:old": error})
                with self.assertRaisesRegex(g.DeploymentError, "private IPv6 connections on port 8080"):
                    g.deploy_balancer(fly, admin, args, fake_tunnel)
                self.assertEqual(fly.events, [])
                self.assertEqual(set(fly.items), {"old"})

    @patch.object(g, "wait_for", once)
    @patch.object(g, "request", return_value={"ready": True, "instanceId": "unfinished-process"})
    def test_retry_finishes_previous_candidate_with_original_image_and_volume(self, _request):
        candidate = balancer("unfinished", "standby")
        candidate["config"]["image"] = "previous-image"
        candidate["config"]["mounts"] = [{"volume": "original-volume", "path": "/data"}]
        fly = FakeFly([candidate])
        admin = FakeAdmin({"private:unfinished": status("unfinished", legacyDependencies=[])})
        args = types.SimpleNamespace(app="app", deployment="new", image="new-image", region="cdg",
                                     secret="secret", public_url="https://balancer")
        g.resume_pending_balancer(fly, admin, args, fake_tunnel)
        self.assertEqual(g.metadata(fly.items["unfinished"])["purr_phase"], "active")
        self.assertEqual(fly.items["unfinished"]["config"]["image"], "previous-image")
        self.assertEqual(fly.items["unfinished"]["config"]["mounts"][0]["volume"], "original-volume")
        self.assertFalse(any(event[0] in ("create", "volume", "destroy") for event in fly.events))

    def test_multiple_unfinished_candidates_block_another_create(self):
        fly = FakeFly([balancer("one", "standby"), balancer("two", "standby")])
        args = types.SimpleNamespace(app="app", deployment="new")
        with self.assertRaisesRegex(g.DeploymentError, "one, two"):
            g.resume_pending_balancer(fly, FakeAdmin({}), args, fake_tunnel)
        self.assertEqual(fly.events, [])

    def test_create_recovers_lost_ack_without_second_post(self):
        existing = relay("new")
        fly = g.Fly("token")
        calls = []
        def fake_call(method, path, body=None):
            calls.append(method)
            if method == "GET":
                return [] if len(calls) == 1 else [existing]
            raise g.DeploymentError("lost create response")
        fly.call = fake_call
        self.assertEqual(fly.create("app", "new", "relay", "cdg", existing["config"])["id"], "new")
        self.assertEqual(calls, ["GET", "POST", "GET"])

    def test_retry_reuses_unattached_generation_volume(self):
        fly = g.Fly("token")
        volume = {"id": "vol_new", "name": g.volume_name("new"), "region": "cdg", "attached_machine_id": None}
        with patch.object(fly, "call", return_value=[volume]) as call:
            self.assertEqual(fly.ensure_volume("app", "new", "cdg"), volume)
            call.assert_called_once_with("GET", "/apps/app/volumes")

    def test_attached_volume_is_never_reused_for_another_machine(self):
        fly = g.Fly("token")
        volume = {"id": "vol_new", "name": g.volume_name("new"), "region": "cdg", "attached_machine_id": "other"}
        with patch.object(fly, "call", return_value=[volume]) as call:
            with self.assertRaises(g.DeploymentError):
                fly.ensure_volume("app", "new", "cdg")
            self.assertEqual(call.call_count, 1)

    def test_volume_deleted_only_after_machine_with_retirement_proof(self):
        machine = balancer("old", "retired")
        machine["state"] = "stopped"
        machine["config"]["metadata"].update(purr_retirement="process", purr_volume="vol_old")
        machine["config"]["mounts"] = [{"volume": "vol_old", "path": "/data"}]
        fly = g.Fly("token")
        calls = []
        def call(method, path, body=None):
            calls.append((method, path))
            if method == "GET" and "/machines/" in path:
                return machine
            if method == "GET":
                return {"id": "vol_old", "name": g.volume_name("old"), "attached_machine_id": None}
        fly.call = call
        fly.stop_destroy("app", machine, FakeAdmin({}))
        self.assertEqual([path for method, path in calls if method == "DELETE"],
                         ["/apps/app/machines/old", "/apps/app/volumes/vol_old"])

    def test_machine_without_retirement_proof_cannot_be_deleted(self):
        fly = g.Fly("token")
        with patch.object(fly, "call", return_value=relay()) as call:
            with self.assertRaises(g.DeploymentError):
                fly.stop_destroy("app", relay(), FakeAdmin({}))
            self.assertEqual(call.call_count, 1)

    def test_new_process_cannot_be_stopped_using_previous_retirement_proof(self):
        machine = relay()
        machine["config"]["metadata"]["purr_retirement"] = "old-process"
        admin = FakeAdmin({"https://relay.example:20000": status(instanceId="restarted-process", retired=False)})
        fly = g.Fly("token")
        with patch.object(fly, "call", return_value=machine) as call:
            with self.assertRaises(g.DeploymentError):
                fly.stop_destroy("app", machine, admin)
            self.assertEqual(call.call_count, 1)


class InitialCutoverTests(unittest.TestCase):
    def setUp(self):
        self.old = balancer("legacy")
        self.old["config"]["metadata"] = {}
        self.fly = FakeFly([self.old])
        self.admin = FakeAdmin({"private:new": status("new", legacyDependencies=[])})
        self.args = types.SimpleNamespace(app="app", deployment="new", image="new-image", region="cdg",
                                          secret="secret", public_url="https://balancer", initial_legacy_cutover=True)
        self.registry = {"servers": [{"region": "france", "apiEndpoint": "https://relay.example"}]}
        self.legacy_status = g.HttpError(404, "https://balancer/admin/status")

    def public_request(self, method, url, body=None, headers=None, **kwargs):
        self.assertEqual(headers["internal_key_secret"], "secret")
        if headers["Fly-Force-Instance-Id"] == "legacy":
            if url.endswith("/admin/status"):
                if isinstance(self.legacy_status, Exception):
                    raise self.legacy_status
                return self.legacy_status
            self.assertTrue(url.endswith("/servers"))
            return self.registry
        self.assertEqual(headers["Fly-Force-Instance-Id"], "new")
        self.fly.events.append(("public-ready", "new"))
        return status("new")

    def deploy(self):
        with patch.object(g, "request", side_effect=self.public_request), patch.object(g, "wait_for", once):
            g.deploy_balancer(self.fly, self.admin, self.args, fake_tunnel)

    def test_initial_cutover_stops_only_verified_balancer_after_successor_is_public(self):
        self.deploy()
        config = self.fly.items["new"]["config"]
        self.assertNotIn("BALANCER_PREDECESSOR_URL", config["env"])
        self.assertEqual(config["metadata"]["purr_initial_legacy_cutover"], "true")
        self.assertEqual(config["metadata"]["purr_predecessor"], "legacy")
        self.assertEqual(config["metadata"]["purr_legacy_image"], "image")
        self.assertEqual(config["metadata"]["purr_phase"], "active")
        self.assertEqual(self.admin.hosts, [("private:new", "new.vm.app.internal:8080")])
        self.assertLess(self.fly.events.index(("public-ready", "new")), self.fly.events.index(("cordon", "legacy")))
        self.assertLess(self.fly.events.index(("cordon", "legacy")), self.fly.events.index(("stop", "legacy")))
        self.assertEqual(self.fly.items["legacy"]["state"], "stopped")
        self.assertEqual(set(self.fly.items), {"legacy", "new"})
        self.assertFalse(any(event[0] == "destroy" for event in self.fly.events))

    def test_created_machine_must_start_before_opening_private_proxy(self):
        original_create = self.fly.create
        def created(*args, **kwargs):
            machine = original_create(*args, **kwargs)
            self.fly.items["new"]["state"] = machine["state"] = "starting"
            return machine
        def wait_started(probe, description, **kwargs):
            if "before private DNS discovery" in description:
                self.assertFalse(probe())
                self.fly.items["new"]["state"] = "started"
                self.assertTrue(probe())
                self.fly.events.append(("machine-started", "new"))
                return True
            return once(probe, description, **kwargs)
        @contextlib.contextmanager
        def after_start(app, machine_id):
            self.assertIn(("machine-started", machine_id), self.fly.events)
            self.assertEqual(self.fly.items[machine_id]["state"], "started")
            yield "private:" + machine_id
        with patch.object(self.fly, "create", side_effect=created), \
                patch.object(g, "wait_for", side_effect=wait_started), \
                patch.object(g, "request", side_effect=self.public_request):
            g.deploy_balancer(self.fly, self.admin, self.args, after_start)

    def test_machine_start_failure_preserves_candidate_and_predecessor(self):
        original_create = self.fly.create
        def created(*args, **kwargs):
            machine = original_create(*args, **kwargs)
            self.fly.items["new"]["state"] = machine["state"] = "starting"
            return machine
        with patch.object(self.fly, "create", side_effect=created), \
                patch.object(g, "wait_for", once), \
                patch.object(g, "request", side_effect=self.public_request):
            with self.assertRaisesRegex(g.DeploymentError, "before private DNS discovery"):
                g.deploy_balancer(self.fly, self.admin, self.args,
                                 lambda *_: self.fail("Proxy opened before Machine started"))
        self.assertEqual(set(self.fly.items), {"legacy", "new"})
        self.assertEqual(g.metadata(self.fly.items["new"])["purr_phase"], "standby")
        self.assertNotIn(("cordon", "legacy"), self.fly.events)
        self.assertNotIn(("stop", "legacy"), self.fly.events)

    def test_ambiguous_or_owned_sources_block_cutover_before_any_mutation(self):
        other_legacy = copy.deepcopy(self.old)
        other_legacy["id"] = "another"
        stopped_owned = balancer("stopped")
        stopped_owned["state"] = "stopped"
        for machines in ([], [self.old, other_legacy], [self.old, balancer("active")], [self.old, stopped_owned]):
            with self.subTest(machines=[machine["id"] for machine in machines]):
                self.fly = FakeFly(machines)
                with self.assertRaises(g.DeploymentError):
                    self.deploy()
                self.assertEqual(self.fly.events, [])

    def test_only_explicit_legacy_404_allows_initial_cutover(self):
        for result in ({"ready": True}, g.HttpError(403, "public"), g.HttpError(500, "public"),
                       g.DeploymentError("public connection failed")):
            with self.subTest(result=result):
                self.legacy_status = result
                with self.assertRaises(g.DeploymentError):
                    self.deploy()
                self.assertEqual(self.fly.events, [])

    def test_invalid_legacy_registry_prevents_fresh_directory(self):
        valid = self.registry["servers"][0]
        for registry in (None, {}, {"servers": []}, {"servers": [valid, valid]},
                         {"servers": [{"region": "france", "apiEndpoint": "file:///unknown"}]}):
            with self.subTest(registry=registry):
                self.registry = registry
                with self.assertRaises(g.DeploymentError):
                    self.deploy()
                self.assertEqual(self.fly.events, [])

    def test_normal_deploy_never_falls_back_to_empty_after_private_failure(self):
        self.args.initial_legacy_cutover = False
        self.admin.statuses["private:legacy"] = g.DeploymentError("private listener unavailable")
        with self.assertRaises(g.DeploymentError):
            self.deploy()
        self.assertEqual(self.fly.events, [])

    def test_unready_successor_or_legacy_dependency_never_stops_predecessor(self):
        for readiness in (status("new", ready=False, legacyDependencies=[]),
                          status("new", legacyDependencies=[{"url": "old"}])):
            with self.subTest(readiness=readiness):
                self.fly = FakeFly([self.old])
                self.admin.statuses["private:new"] = readiness
                with self.assertRaises(g.DeploymentError):
                    self.deploy()
                self.assertEqual(self.fly.items["legacy"]["state"], "started")
                self.assertNotIn(("cordon", "legacy"), self.fly.events)
                self.assertNotIn(("stop", "legacy"), self.fly.events)

    def test_retry_after_lost_stop_ack_uses_recorded_mode_with_flag_disabled(self):
        original_action = self.fly.action
        def lost_stop_ack(app, machine_id, action, body=None):
            original_action(app, machine_id, action, body)
            if action == "stop":
                raise g.DeploymentError("lost stop response")
        with patch.object(self.fly, "action", side_effect=lost_stop_ack):
            with self.assertRaisesRegex(g.DeploymentError, "lost stop response"):
                self.deploy()
        self.assertEqual(g.metadata(self.fly.items["new"])["purr_phase"], "standby")
        self.assertEqual(self.fly.items["legacy"]["state"], "stopped")
        self.args.initial_legacy_cutover = False
        self.args.deployment, self.args.image = "retry", "next-image"
        with patch.object(g, "request", side_effect=self.public_request), patch.object(g, "wait_for", once):
            self.assertTrue(g.resume_pending_balancer(self.fly, self.admin, self.args, fake_tunnel))
        self.assertEqual(g.metadata(self.fly.items["new"])["purr_phase"], "active")
        self.assertEqual(self.fly.items["new"]["config"]["image"], "new-image")
        self.assertEqual(self.fly.events.count(("stop", "legacy")), 1)
        self.assertEqual(self.fly.events.count(("volume", "new")), 1)
        self.assertEqual(self.fly.events.count(("create", "new", True)), 1)
        self.assertEqual(set(self.fly.items), {"legacy", "new"})

    def test_changed_source_image_is_not_stopped(self):
        original_request = self.public_request
        def change_source(*args, **kwargs):
            result = original_request(*args, **kwargs)
            if self.fly.events and self.fly.events[-1] == ("public-ready", "new"):
                self.fly.items["legacy"]["config"]["image"] = "unexpected-update"
            return result
        with patch.object(g, "request", side_effect=change_source), patch.object(g, "wait_for", once):
            with self.assertRaisesRegex(g.DeploymentError, "source changed"):
                g.deploy_balancer(self.fly, self.admin, self.args, fake_tunnel)
        self.assertNotIn(("cordon", "legacy"), self.fly.events)
        self.assertNotIn(("stop", "legacy"), self.fly.events)

    def test_new_action_resumes_cutover_then_uses_normal_handoff_even_if_flag_stays_true(self):
        self.old["state"] = "stopped"
        pending = balancer("unfinished", "standby")
        pending["config"]["metadata"].update(purr_initial_legacy_cutover="true",
                                               purr_predecessor="legacy", purr_legacy_image="image")
        pending["config"]["mounts"] = [{"volume": "original-volume", "path": "/data"}]
        self.fly = FakeFly([self.old, pending])
        self.admin.statuses["private:unfinished"] = status("unfinished", legacyDependencies=[])
        def public_status(method, url, body=None, headers=None, **kwargs):
            return status(headers["Fly-Force-Instance-Id"])
        with patch.object(g, "request", side_effect=public_status), patch.object(g, "wait_for", once):
            g.deploy_balancer(self.fly, self.admin, self.args, fake_tunnel)
        new_config = self.fly.items["new"]["config"]
        self.assertEqual(new_config["env"]["BALANCER_PREDECESSOR_URL"], "http://unfinished.vm.app.internal:8080")
        self.assertNotIn("purr_initial_legacy_cutover", new_config["metadata"])
        self.assertEqual(g.metadata(self.fly.items["unfinished"])["purr_phase"], "draining")
        self.assertEqual(self.fly.items["unfinished"]["config"]["mounts"][0]["volume"], "original-volume")
        self.assertNotIn(("stop", "legacy"), self.fly.events)


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""Deploy immutable Machines without replacing room-bearing relay processes."""

import argparse
import contextlib
import datetime
import hashlib
import html
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


REGIONS = (("cdg", "france"), ("gru", "brazil"), ("jnb", "south-africa"),
           ("ewr", "nj-us"), ("nrt", "japan"), ("syd", "australia"), ("sin", "china"))
OWNER = "purr-generations-v1"
PORT_FIRST, PORT_LAST, PORT_COUNT = 20000, 59999, 5


class DeploymentError(RuntimeError):
    pass


class MachineRetiredError(DeploymentError):
    pass


class CleanupReport:
    def __init__(self, apps=()):
        self.rows = {}
        self.apps = dict.fromkeys(apps, "Not checked: cleanup has not reached this app")
        self.started_at = datetime.datetime.now(datetime.timezone.utc).isoformat()

    @staticmethod
    def text(value):
        value = str(value)
        for key, secret in os.environ.items():
            if secret and re.search(r"secret|token|password|authorization|api[-_]?key", key, re.IGNORECASE):
                value = value.replace(secret, "[redacted]")
        return " ".join("".join(c for c in value if c.isprintable() or c.isspace()).split())[:1024]

    def track(self, app, machines, role):
        self.apps[app] = f"Inventoried {len(machines)} machines"
        for machine in machines:
            tags = metadata(machine)
            self.rows[(app, machine["id"])] = {
                "app": self.text(app), "machine": self.text(machine["id"]), "role": role,
                "state": self.text(machine.get("state", "unknown")),
                "phase": self.text(tags.get("purr_phase", "unmanaged")),
                "deployment": self.text(tags.get("purr_deployment", "legacy / unknown")),
                "decision": "Not checked", "reason": "Cleanup stopped before evaluating this machine",
                "activity": "Not queried"}

    def record(self, app, machine, decision, reason, status=None):
        row = self.rows[(app, machine["id"])]
        row.update(decision=decision, reason=self.text(reason))
        if status is not None and row["role"] == "relay":
            counts = []
            for key, label in (("roomCount", "rooms"), ("pendingAllocations", "reservations"),
                               ("transportConnections", "transport connections"),
                               ("pipeConnections", "pipe connections"), ("pendingOffers", "offers")):
                value = status.get(key)
                counts.append(f"{label}: {value if type(value) is int and value >= 0 else '?'}")
            row["activity"] = "; ".join(counts)
        print(f"Cleanup: {row['app']}/{row['machine']}: {decision}: {row['reason']}; {row['activity']}")

    def markdown(self):
        def cell(value):
            value = html.escape(self.text(value), quote=False)
            return re.sub(r"([\\`*_[\]{}|])", r"\\\1", value)

        totals = {decision: sum(r["decision"] == decision for r in self.rows.values())
                  for decision in ("Retired", "Retained", "Needs review", "Not checked")}
        lines = ["# Purr Transport drain report", "", f"Run started: {self.started_at}", "",
                 " · ".join(f"**{count} {decision.lower()}**" for decision, count in totals.items()), "",
                 "Retired means Machine deletion was acknowledged. State and phase are from the initial inventory.",
                 "Activity is the last retirement check, not a live player count; `?` means unavailable, not zero.", "",
                 "| App | Inspection |", "| --- | --- |"]
        lines.extend(f"| {cell(app)} | {cell(note)} |" for app, note in self.apps.items())
        lines += ["", "| App / Machine | State / phase before | Generation | Decision | Why | Activity |",
                  "| --- | --- | --- | --- | --- | --- |"]
        for row in self.rows.values():
            values = (row["app"] + " / " + row["machine"], row["state"] + " / " + row["phase"],
                      row["deployment"], row["decision"], row["reason"], row["activity"])
            lines.append("| " + " | ".join(cell(value) for value in values) + " |")
        if not self.rows:
            lines += ["", "No Machine rows to show. The app inspections above distinguish empty inventories "
                      "from apps that could not be checked."]
        lines += ["", "Managed generations retire automatically after confirming they are empty. "
                  "Legacy/unmanaged Machines cannot supply that proof and need a separate one-time review. "
                  "They are not automatically removed even if idle.", "",
                  "A successful workflow can retain Machines. Read 'Needs review' and incomplete app inspections "
                  "for API errors or missing proof. This report does not force retirement.", ""]
        return "\n".join(lines)

    def write(self, directory=None):
        summary = self.markdown()
        if directory:
            path = Path(directory)
            path.mkdir(parents=True, exist_ok=True)
            (path / "drain-report.md").write_text(summary, encoding="utf-8")
            (path / "drain-report.json").write_text(json.dumps({
                "startedAt": self.started_at, "apps": {self.text(k): self.text(v) for k, v in self.apps.items()},
                "machines": list(self.rows.values())}, indent=2) + "\n", encoding="utf-8")
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as output:
                output.write(summary)
        print(summary)


class HttpError(DeploymentError):
    def __init__(self, status, url, detail=None):
        self.status = status
        super().__init__(f"HTTP {status}: {url}" + (f": {detail}" if detail else ""))


def machine_api_error_detail(error, body, headers):
    try:
        raw = error.read(8193)
        if len(raw) > 8192:
            return None
        response = json.loads(raw)
    except (OSError, ValueError):
        return None
    finally:
        error.close()
    if not isinstance(response, dict):
        return None
    detail = response.get("error") or response.get("message")
    if isinstance(detail, dict):
        detail = detail.get("message")
    if not isinstance(detail, str):
        return None

    sensitive = re.compile(r"secret|token|password|authorization|api[-_]?key|private[-_]?key", re.IGNORECASE)
    credentials = set()
    def collect(value, marked=False):
        if isinstance(value, dict):
            named_header = sensitive.search(str(value.get("name", "")))
            for key, item in value.items():
                collect(item, marked or bool(sensitive.search(str(key))) or bool(named_header and key == "values"))
        elif isinstance(value, list):
            for item in value:
                collect(item, marked)
        elif marked and isinstance(value, str) and value:
            credentials.add(value)
            if value.startswith("Bearer "):
                credentials.add(value[7:])
    collect(body)
    collect(headers or {})
    collect(dict(os.environ))
    for credential in sorted(credentials, key=len, reverse=True):
        for encoded in (credential, json.dumps(credential)[1:-1], urllib.parse.quote(credential, safe="")):
            detail = detail.replace(encoded, "<redacted>")
    # API error text can echo a submitted config; never include structured payloads.
    detail = re.split(r"[\[{]", detail, maxsplit=1)[0]
    detail = re.sub(r"(?i)\b(?:Bearer|FlyV1)\s+\S+", "[credential omitted]", detail)
    detail = re.sub(r"(?i)\b(?:secret|token|password|authorization|api[-_]?key)\s*[:=]\s*\S+",
                    "[credential omitted]", detail)
    detail = " ".join("".join(char for char in detail if char.isprintable() or char.isspace()).split())
    return detail[:512] if detail else None


def request(method, url, body=None, headers=None, timeout=30):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method=method, headers={
        "Content-Type": "application/json", **(headers or {})})
    try:
        # Never follow redirects carrying either the Fly token or internal secret.
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                return None
        with urllib.request.build_opener(NoRedirect).open(req, timeout=timeout) as response:
            data = response.read(16 * 1024 * 1024 + 1)
            if len(data) > 16 * 1024 * 1024:
                raise DeploymentError(f"Response too large: {url}")
            return json.loads(data) if data else None
    except urllib.error.HTTPError as error:
        detail = None
        if urllib.parse.urlsplit(url).hostname == "api.machines.dev":
            detail = machine_api_error_detail(error, body, headers)
        else:
            error.close()
        raise HttpError(error.code, url, detail) from None
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        raise DeploymentError(f"Request failed: {url}: {type(error).__name__}") from None


def wait_for(probe, description, timeout=180, interval=3):
    deadline = time.monotonic() + timeout
    last_error = None
    while True:
        try:
            result = probe()
            if result:
                return result
        except (DeploymentError, OSError) as error:
            last_error = error
        if time.monotonic() >= deadline:
            raise DeploymentError(f"Timed out waiting for {description}: {last_error or 'not ready'}")
        time.sleep(interval)


def metadata(machine):
    return machine.get("config", {}).get("metadata", {})


def owned(machine, role):
    tags = metadata(machine)
    return tags.get("purr_owner") == OWNER and tags.get("purr_role") == role


def select_port_block(machines):
    occupied = set()
    for machine in machines:
        if machine.get("config") is None or machine.get("incomplete_config") is not None:
            raise DeploymentError("An existing Machine has incomplete config; refusing port allocation")
        for service in machine.get("config", {}).get("services", []):
            for port in service.get("ports", []):
                if port.get("port") is not None:
                    occupied.add(int(port["port"]))
                elif port.get("start_port") is not None and port.get("end_port") is not None:
                    occupied.update(range(int(port["start_port"]), int(port["end_port"]) + 1))
                else:
                    raise DeploymentError("Unknown existing service port definition; refusing port allocation")
    for base in range(PORT_FIRST, PORT_LAST - PORT_COUNT + 2, PORT_COUNT):
        if occupied.isdisjoint(range(base, base + PORT_COUNT)):
            return base
    raise DeploymentError("All relay generation port blocks are reserved")


def service(protocol, internal, external, handlers=None):
    port = {"port": external}
    if handlers:
        port["handlers"] = handlers
    return {"protocol": protocol, "internal_port": internal, "ports": [port],
            "autostop": "off", "autostart": False}


def machine_config(image, role, deployment, env):
    return {"image": image, "env": env, "auto_destroy": False,
            "restart": {"policy": "always"},
            "guest": {"cpu_kind": "shared", "cpus": 1, "memory_mb": 512},
            "metadata": {"purr_owner": OWNER, "purr_role": role,
                         "purr_deployment": deployment, "purr_phase": "standby"}}


def volume_name(deployment):
    return "purr_bal_" + hashlib.sha256(deployment.encode()).hexdigest()[:20]


def relay_config(image, deployment, host, region, balancer, public_ip, base, secret):
    endpoint = f"https://{host}:{base}"
    config = machine_config(image, "relay", deployment, {
        "HOST": "+", "PORT": "8081", "HOST_DOMAIN": host, "HOST_SSL": "true",
        "HOST_ENDPOINT": endpoint, "HOST_REGION": region, "BALANCER_URL": balancer,
        "UDP_PORT": str(base + 2), "UDP_PORT_V2": str(base + 3),
        "WEBSOCKETS_PORT": str(base + 1), "WEBRTC_ENABLED": "true",
        "WEBRTC_UDP_PORT": str(base + 4), "WEBRTC_PUBLIC_IP": public_ip,
        "RELAY_DEPLOYMENT_ID": deployment, "RELAY_START_STANDBY": "true", "SECRET": secret})
    if region == "france":
        # Avoid shared-CPU quota throttling; performance CPUs require at least 2 GB.
        config["guest"] = {"cpu_kind": "performance", "cpus": 1, "memory_mb": 2048}
    config["services"] = [service("tcp", 8081, base, ["tls", "http"]),
                          service("tcp", base + 1, base + 1, ["tls"])]
    config["services"] += [service("udp", port, port) for port in range(base + 2, base + 5)]
    config["metadata"]["purr_endpoint"] = endpoint
    return config


def balancer_config(image, deployment, predecessor, secret):
    env = {"HOST": "+", "PORT": "8080", "SECRET": secret,
           "BALANCER_DEPLOYMENT_ID": deployment,
           "BALANCER_STATE_PATH": "/data/balancer-state.json"}
    if predecessor:
        env["BALANCER_PREDECESSOR_URL"] = predecessor
    config = machine_config(image, "balancer", deployment, env)
    http = service("tcp", 8080, 443, ["tls", "http"])
    http["ports"].append({"port": 80, "handlers": ["http"], "force_https": True})
    http["checks"] = [{"type": "http", "port": 8080, "protocol": "http",
                        "method": "GET", "path": "/admin/ready",
                        "interval": "5s", "timeout": "2s", "grace_period": "10s",
                        "headers": [{"name": "internal_key_secret", "values": [secret]}]}]
    config["services"] = [http]
    return config


class Fly:
    def __init__(self, token):
        self.headers = {"Authorization": f"Bearer {token}"}

    def call(self, method, path, body=None):
        return request(method, "https://api.machines.dev/v1" + path, body, self.headers)

    def machines(self, app):
        return self.call("GET", f"/apps/{app}/machines")

    def get(self, app, machine_id):
        return self.call("GET", f"/apps/{app}/machines/{machine_id}")

    def action(self, app, machine_id, action, body=None):
        return self.call("POST", f"/apps/{app}/machines/{machine_id}/{action}", body)

    def tag(self, app, machine_id, key, value):
        for attempt in range(5):
            try:
                return self.action(app, machine_id, f"metadata/{key}", {"value": str(value)})
            except HttpError as error:
                if error.status != 429 or attempt == 4:
                    raise
                delay = 2 ** attempt
                print(f"Fly rate limit updating metadata on {app}/{machine_id}; "
                      f"retrying in {delay}s ({attempt + 1}/4)")
                time.sleep(delay)

    def ensure_volume(self, app, deployment, region):
        name = volume_name(deployment)
        def find():
            matches = [v for v in self.call("GET", f"/apps/{app}/volumes") if v.get("name") == name]
            if not matches:
                return None
            if len(matches) != 1 or matches[0].get("region") != region or matches[0].get("attached_machine_id"):
                raise DeploymentError(f"Generation volume is ambiguous, attached or in another region: {name}")
            return matches[0]
        existing = find()
        if existing:
            return existing
        try:
            return self.call("POST", f"/apps/{app}/volumes", {
                "name": name, "region": region, "size_gb": 1, "encrypted": True,
                "compute": {"cpu_kind": "shared", "cpus": 1, "memory_mb": 512}})
        except DeploymentError:
            for _ in range(6):
                existing = find()
                if existing:
                    return existing
                time.sleep(2)
            raise

    def create(self, app, deployment, role, region, config, cordoned=False):
        name = f"purr-{role}-{deployment}"
        existing = [m for m in self.machines(app) if m.get("name") == name]
        if existing:
            if len(existing) != 1 or not owned(existing[0], role):
                raise DeploymentError(f"Machine name collision: {name}")
            if existing[0]["config"]["image"] != config["image"]:
                raise DeploymentError(f"Existing generation uses a different image: {name}")
            return existing[0]
        try:
            return self.call("POST", f"/apps/{app}/machines", {
                "name": name, "region": region, "config": config,
                "skip_service_registration": cordoned})
        except DeploymentError:
            # A timed-out create may already have succeeded. Never blindly POST twice.
            for _ in range(6):
                matches = [m for m in self.machines(app) if m.get("name") == name]
                if (len(matches) == 1 and owned(matches[0], role) and
                        matches[0]["config"]["image"] == config["image"]):
                    return matches[0]
                time.sleep(2)
            raise

    def stop_destroy(self, app, machine, admin, open_tunnel=None):
        machine_id = machine["id"]
        current = self.get(app, machine_id)
        tags = metadata(current)
        if (not tags.get("purr_retirement") or
                tags.get("purr_retirement") != metadata(machine).get("purr_retirement") or
                tags.get("purr_owner") != OWNER or
                tags.get("purr_deployment") != metadata(machine).get("purr_deployment")):
            raise DeploymentError(f"Retirement proof changed for {app}/{machine_id}")
        volume_id = tags.get("purr_volume") if owned(current, "balancer") else None
        if volume_id:
            mounts = current["config"].get("mounts", [])
            if len(mounts) != 1 or mounts[0].get("volume") != volume_id or mounts[0].get("path") != "/data":
                raise DeploymentError(f"Generation volume mount changed for {app}/{machine_id}")
        if current["state"] != "stopped":
            if owned(current, "relay"):
                status = admin.status(tags["purr_endpoint"])
                sealed = status.get("retired") is True and status.get("canRetire") is True
            else:
                with (open_tunnel or tunnel)(app, machine_id) as endpoint:
                    status = admin.status(endpoint, host=private_host(app, machine_id))
                sealed = status.get("canRetire") is True and bool(status.get("successorUrl"))
            if not sealed or status.get("instanceId") != tags["purr_retirement"]:
                raise DeploymentError(f"Retired process changed before stop: {app}/{machine_id}")
            self.action(app, machine_id, "stop", {"signal": "SIGTERM", "timeout": "30s"})
            wait_for(lambda: self.get(app, machine_id)["state"] == "stopped",
                     f"{app}/{machine_id} to stop", timeout=90)
        self.call("DELETE", f"/apps/{app}/machines/{machine_id}")
        print(f"Retired {app}/{machine_id}")
        if volume_id:
            try:
                volume = self.call("GET", f"/apps/{app}/volumes/{volume_id}")
                if volume.get("attached_machine_id") or volume.get("name") != volume_name(tags["purr_deployment"]):
                    raise DeploymentError(f"Retaining unexpected/attached volume {volume_id}")
                self.call("DELETE", f"/apps/{app}/volumes/{volume_id}")
            except DeploymentError as error:
                raise MachineRetiredError(f"Machine deleted; volume {volume_id} cleanup unconfirmed; "
                                          f"inspect it manually: {error}") from error
            print(f"Deleted retired generation volume {volume_id}")


class Admin:
    def __init__(self, secret):
        self.headers = {"internal_key_secret": secret}

    def call(self, url, path, body=None, instance=None, method="GET", host=None):
        headers = dict(self.headers)
        if host:
            headers["Host"] = host
        if instance:
            headers["relay_instance_id"] = instance
        return request(method, url.rstrip("/") + path, body, headers)

    def status(self, url, host=None):
        return self.call(url, "/admin/status", host=host)

    def mutate(self, url, operation, instance):
        return self.call(url, "/admin/" + operation, instance=instance, method="POST")


def private_host(app, machine_id):
    return f"{machine_id}.vm.{app}.internal:8080"


def private_url(app, machine_id):
    return "http://" + private_host(app, machine_id)


def stop_proxy(process):
    try:
        process.stdin.close()
    except OSError:
        pass
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


@contextlib.contextmanager
def tunnel(app, machine_id, startup_timeout=180, retry_interval=2, *, clock=time.monotonic, sleep=time.sleep):
    deadline = clock() + startup_timeout
    delay = retry_interval
    last_error = "not listening"
    host = f"{machine_id}.vm.{app}.internal"
    while clock() < deadline:
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        process = subprocess.Popen(["flyctl", "proxy", f"{port}:8080", host, "--app", app,
                                    "--bind-addr", "127.0.0.1", "--watch-stdin"],
                                   stdin=subprocess.PIPE, stdout=subprocess.DEVNULL)
        try:
            attempt_deadline = min(deadline, clock() + 30)
            while clock() < attempt_deadline:
                if process.poll() is not None:
                    last_error = f"proxy exited with code {process.returncode}"
                    break
                try:
                    probe_timeout = min(0.2, max(0.01, attempt_deadline - clock()))
                    with socket.create_connection(("127.0.0.1", port), timeout=probe_timeout):
                        pass
                except OSError as error:
                    last_error = type(error).__name__
                else:
                    if process.poll() is None:
                        yield f"http://127.0.0.1:{port}"
                        return
                sleep(min(0.2, max(0, attempt_deadline - clock())))
        finally:
            stop_proxy(process)
        remaining = deadline - clock()
        if remaining > 0:
            print(f"Retrying private proxy for {app}/{machine_id}: {last_error}", file=sys.stderr)
            sleep(min(delay, remaining))
            delay = min(delay * 2, 10)
    raise DeploymentError(f"Timed out starting private proxy for {app}/{machine_id}: {last_error}")


def status_ready(admin, endpoint, deployment=None, host=None):
    status = admin.status(endpoint, host=host)
    if status.get("ready") is not True:
        return None
    if deployment is not None and status.get("deploymentId") != deployment:
        raise DeploymentError(f"Wrong deployment at {endpoint}")
    if not status.get("instanceId"):
        raise DeploymentError(f"Missing process identity at {endpoint}")
    return status


def find_predecessor(fly, admin, app, exclude=None, open_tunnel=tunnel):
    machines = fly.machines(app)
    managed = [machine for machine in machines if machine["id"] != exclude and owned(machine, "balancer")]
    if managed:
        for machine in machines:
            if machine["id"] != exclude and not owned(machine, "balancer"):
                print(f"Retaining unmanaged Machine {app}/{machine['id']}; not a predecessor candidate "
                      "while managed balancer generations exist")
    leaders, legacy = [], []
    for machine in managed if managed else machines:
        if machine["id"] == exclude or machine["state"] != "started":
            continue
        if not any(s.get("internal_port") == 8080 for s in machine.get("config", {}).get("services", [])):
            continue
        try:
            with open_tunnel(app, machine["id"]) as endpoint:
                try:
                    status = admin.status(endpoint, host=private_host(app, machine["id"]))
                except HttpError as error:
                    if error.status != 404 or owned(machine, "balancer"):
                        raise
                    legacy.append(machine)
                    continue
                if status.get("ready") is True and not status.get("successorUrl"):
                    leaders.append(machine)
        except HttpError:
            raise
        except (DeploymentError, OSError) as error:
            raise DeploymentError(
                f"Cannot reach predecessor {app}/{machine['id']} at {private_url(app, machine['id'])}. "
                "Live handoff requires its HTTP listener to accept Fly private IPv6 connections on port 8080; "
                "an IPv4-only listener is insufficient. Verify that listener and private connectivity before retrying. "
                f"No successor was created; preserving all Machines. Original error: {error}") from error
    if len(leaders) == 1:
        print(f"Authoritative predecessor {app}/{leaders[0]['id']}")
        return leaders[0]
    if not managed and not leaders and len(legacy) == 1:
        return legacy[0]
    if not leaders and not legacy and not machines:
        return None
    raise DeploymentError("Cannot identify exactly one authoritative predecessor; preserving all Machines")


def initial_legacy_predecessor(fly, admin, args):
    machines = fly.machines(args.app)
    if any(metadata(machine).get("purr_owner") == OWNER for machine in machines):
        raise DeploymentError("Initial legacy cutover is only for the first migration; managed generations already exist "
                              "(including stopped Machines). Rerun Deploy Purr Transport with initial_legacy_cutover "
                              "disabled. Do not delete existing generations to bypass this check.")
    candidates = [machine for machine in machines if machine["state"] == "started" and
                  any(service.get("internal_port") == 8080
                      for service in machine.get("config", {}).get("services", []))]
    if len(candidates) != 1 or metadata(candidates[0]).get("purr_owner"):
        raise DeploymentError("Initial legacy cutover requires exactly one started unowned balancer")
    predecessor = candidates[0]
    if not predecessor.get("config", {}).get("image"):
        raise DeploymentError("Cannot verify the legacy predecessor image")
    headers = {**admin.headers, "Fly-Force-Instance-Id": predecessor["id"]}
    try:
        request("GET", args.public_url + "/admin/status", headers=headers)
    except HttpError as error:
        if error.status != 404:
            raise
    else:
        raise DeploymentError("Initial cutover only accepts a verified legacy balancer without the handoff API")
    registry = request("GET", args.public_url + "/servers", headers=headers)
    servers = registry.get("servers") if isinstance(registry, dict) else None
    if not isinstance(servers, list) or not servers:
        raise DeploymentError("Legacy predecessor did not return a nonempty relay registry")
    regions, endpoints = set(), set()
    for server in servers:
        if not isinstance(server, dict):
            raise DeploymentError("Legacy predecessor returned an invalid relay registry")
        region, endpoint = server.get("region"), server.get("apiEndpoint")
        if not isinstance(region, str) or not region.strip() or not isinstance(endpoint, str):
            raise DeploymentError("Legacy relay region or endpoint is missing")
        try:
            parsed = urllib.parse.urlsplit(endpoint)
            parsed.port
        except ValueError as error:
            raise DeploymentError("Legacy relay endpoint is malformed") from error
        if (parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password or
                parsed.query or parsed.fragment or region in regions or endpoint in endpoints):
            raise DeploymentError("Legacy relay ownership is invalid or ambiguous")
        regions.add(region)
        endpoints.add(endpoint)
    print(f"Verified initial legacy cutover source {args.app}/{predecessor['id']}: "
          f"{len(servers)} regional relays; its room directory will not be transferred")
    return predecessor


def finish_initial_legacy_cutover(fly, args, machine):
    tags = metadata(machine)
    predecessor_id = tags.get("purr_predecessor")
    image = tags.get("purr_legacy_image")
    if not predecessor_id or not image:
        raise DeploymentError("Initial cutover recovery metadata is incomplete")
    predecessor = fly.get(args.app, predecessor_id)
    if metadata(predecessor).get("purr_owner") or predecessor.get("config", {}).get("image") != image:
        raise DeploymentError("Legacy cutover source changed; retaining it for inspection")
    if predecessor["state"] not in ("started", "stopping", "stopped"):
        raise DeploymentError("Legacy cutover source is in an unexpected state; retaining it")
    fly.action(args.app, predecessor_id, "cordon")
    if predecessor["state"] == "started":
        fly.action(args.app, predecessor_id, "stop", {"signal": "SIGTERM", "timeout": "30s"})
    wait_for(lambda: fly.get(args.app, predecessor_id)["state"] == "stopped",
             f"legacy balancer {args.app}/{predecessor_id} to stop", timeout=90)
    print(f"Stopped and retained initial legacy balancer {args.app}/{predecessor_id}; regional relay Machines remain running")


def resume_pending_balancer(fly, admin, args, open_tunnel):
    pending = [m for m in fly.machines(args.app) if owned(m, "balancer")
               and metadata(m).get("purr_phase") == "standby"
               and metadata(m).get("purr_deployment") != args.deployment]
    if len(pending) > 1:
        names = ", ".join(m["id"] for m in pending)
        raise DeploymentError(f"Multiple unfinished balancer Machines ({names}); preserve them and resolve the pending handoff before creating another")
    if pending:
        machine = pending[0]
        resumed = argparse.Namespace(**vars(args))
        resumed.deployment = metadata(machine)["purr_deployment"]
        resumed.image = machine["config"]["image"]
        print(f"Resuming unfinished balancer {args.app}/{machine['id']} with its original image and volume")
        deploy_balancer(fly, admin, resumed, open_tunnel, resume_pending=False)
        return metadata(machine).get("purr_initial_legacy_cutover") == "true"
    return False


def deploy_balancer(fly, admin, args, open_tunnel=tunnel, resume_pending=True):
    initial_cutover = bool(getattr(args, "initial_legacy_cutover", False))
    if resume_pending and resume_pending_balancer(fly, admin, args, open_tunnel):
        initial_cutover = False
    candidates = [m for m in fly.machines(args.app) if owned(m, "balancer")
                  and metadata(m).get("purr_deployment") == args.deployment]
    if len(candidates) > 1:
        raise DeploymentError("Duplicate balancer generation")
    existing = candidates[0] if candidates else None
    if existing:
        machine = existing
        initial_cutover = metadata(machine).get("purr_initial_legacy_cutover") == "true"
        if machine["config"]["image"] != args.image:
            raise DeploymentError("Existing balancer generation uses a different image")
        predecessor_id = metadata(machine).get("purr_predecessor")
        if machine["state"] == "stopped":
            fly.action(args.app, machine["id"], "start")
    else:
        predecessor = (initial_legacy_predecessor(fly, admin, args) if initial_cutover else
                       find_predecessor(fly, admin, args.app, open_tunnel=open_tunnel))
        predecessor_id = predecessor["id"] if predecessor else None
        config = balancer_config(args.image, args.deployment,
                                 private_url(args.app, predecessor_id) if predecessor_id and not initial_cutover else None,
                                 args.secret)
        volume = fly.ensure_volume(args.app, args.deployment, args.region)
        config["mounts"] = [{"volume": volume["id"], "path": "/data"}]
        config["metadata"]["purr_volume"] = volume["id"]
        if predecessor_id:
            config["metadata"]["purr_predecessor"] = predecessor_id
        if initial_cutover:
            config["metadata"].update(purr_initial_legacy_cutover="true",
                                       purr_legacy_image=predecessor["config"]["image"])
        machine = fly.create(args.app, args.deployment, "balancer", args.region, config, cordoned=True)
    wait_for(lambda: fly.get(args.app, machine["id"])["state"] == "started",
             f"balancer {args.app}/{machine['id']} to start before private DNS discovery", timeout=180)
    with open_tunnel(args.app, machine["id"]) as endpoint:
        status = wait_for(lambda: status_ready(admin, endpoint, args.deployment,
                                              host=private_host(args.app, machine["id"])),
                          f"balancer handoff on {args.app}/{machine['id']} (retain this Machine and volume if recovery is needed)", timeout=300)
        if status.get("successorUrl"):
            raise DeploymentError("This deployment is already superseded")
        if initial_cutover and (status.get("legacyDependencies") != [] or
                                machine["config"]["env"].get("BALANCER_PREDECESSOR_URL")):
            raise DeploymentError("Initial cutover successor unexpectedly depends on a predecessor")
        fly.action(args.app, machine["id"], "uncordon")
        def public_ready():
            public_status = request("GET", args.public_url + "/admin/status", headers={
                **admin.headers, "Fly-Force-Instance-Id": machine["id"]})
            return (public_status.get("ready") is True and
                    public_status.get("instanceId") == status["instanceId"])
        wait_for(public_ready, "new balancer public routing", timeout=120)
        if initial_cutover:
            finish_initial_legacy_cutover(fly, args, machine)
        elif predecessor_id:
            fly.action(args.app, predecessor_id, "cordon")
            predecessor = fly.get(args.app, predecessor_id)
            if owned(predecessor, "balancer"):
                fly.tag(args.app, predecessor_id, "purr_phase", "draining")
            print(f"Retained predecessor {args.app}/{predecessor_id}; private handoff dependencies stay reachable")
        # A retry must finish predecessor finalization before creating another successor.
        fly.tag(args.app, machine["id"], "purr_phase", "active")
        print(f"Active balancer {args.app}/{machine['id']}; legacy dependencies: "
              f"{len(status.get('legacyDependencies', []))}")


def active_endpoints(status):
    if status.get("ready") is not True or status.get("successorUrl"):
        raise DeploymentError("Balancer is not the ready authority")
    servers = status.get("relayServers")
    if not isinstance(servers, list):
        raise DeploymentError("Balancer did not provide relay ownership state")
    return {s["apiEndpoint"] for s in servers if s.get("draining") is not True}


def deploy_relay(fly, admin, args):
    machines = fly.machines(args.app)
    existing = [m for m in machines if owned(m, "relay")
                and metadata(m).get("purr_deployment") == args.deployment]
    if len(existing) > 1:
        raise DeploymentError("Duplicate relay generation")
    if existing:
        machine = existing[0]
        if machine["config"]["image"] != args.image:
            raise DeploymentError("Existing relay generation uses a different image")
    else:
        base = select_port_block(machines)
        config = relay_config(args.image, args.deployment, args.domain, args.prefix,
                              args.balancer, args.public_ip, base, args.secret)
        machine = fly.create(args.app, args.deployment, "relay", args.region, config)
    endpoint = metadata(machine)["purr_endpoint"]
    status = wait_for(lambda: status_ready(admin, endpoint, args.deployment), "relay readiness", timeout=300)
    if status.get("retired"):
        raise DeploymentError("This relay generation has already retired")
    status = admin.mutate(endpoint, "activate", status["instanceId"])
    if status.get("draining") is not False or status.get("retired"):
        raise DeploymentError("Relay did not confirm activation")

    def activate_registration():
        admin.call(args.balancer, "/admin/activateRelay",
                   {"apiEndpoint": endpoint, "instanceId": status["instanceId"]}, method="POST")
        return endpoint in active_endpoints(admin.status(args.balancer))
    wait_for(activate_registration, "balancer relay registration and activation", timeout=150)
    fly.tag(args.app, machine["id"], "purr_phase", "active")
    print(f"Active relay {args.app}/{machine['id']} at {endpoint}")
    for old in machines:
        if old["id"] == machine["id"]:
            continue
        if not owned(old, "relay"):
            print(f"Retaining legacy relay {args.app}/{old['id']}; no supported retirement protocol")
            continue
        old_endpoint = metadata(old).get("purr_endpoint")
        if not old_endpoint or old["state"] != "started":
            continue
        old_status = admin.status(old_endpoint)
        if old_status.get("deploymentId") != metadata(old).get("purr_deployment"):
            raise DeploymentError(f"Old relay identity changed: {old_endpoint}")
        drained = admin.mutate(old_endpoint, "drain", old_status["instanceId"])
        if drained.get("draining") is not True:
            raise DeploymentError(f"Relay did not confirm draining: {old_endpoint}")
        fly.tag(args.app, old["id"], "purr_phase", "draining")
        print(f"Draining {args.app}/{old['id']}: {drained.get('roomCount')} rooms")


def prune_relays(fly, admin, app, balancer, report=None):
    report = report if report is not None else CleanupReport()
    machines = fly.machines(app)
    report.track(app, machines, "relay")
    registry = admin.status(balancer)
    active = active_endpoints(registry)
    for machine in machines:
        tags = metadata(machine)
        endpoint = tags.get("purr_endpoint")
        if not owned(machine, "relay"):
            report.record(app, machine, "Retained", "Legacy/unmanaged Machine: no supported retirement proof; "
                          "requires a separate one-time review, not automatic cleanup")
            continue
        if not endpoint:
            report.record(app, machine, "Needs review", "Missing relay endpoint metadata; cannot verify retirement")
            continue
        if endpoint in active:
            report.record(app, machine, "Retained", "Active relay endpoint in the authoritative balancer; excluded from retirement")
            continue
        registrations = [s for s in registry["relayServers"] if s.get("apiEndpoint") == endpoint]
        superseded = (len(registrations) == 1 and registrations[0].get("draining") is True and
                      registrations[0].get("deploymentId") == tags.get("purr_deployment"))
        if tags.get("purr_phase") not in ("draining", "retired") and not superseded:
            report.record(app, machine, "Retained", "Not marked draining/retired and no matching registry proof "
                          "that this generation was superseded; an absent registration is not proof")
            continue
        status = None
        try:
            if machine["state"] == "stopped" and tags.get("purr_retirement"):
                fly.stop_destroy(app, machine, admin)
                report.record(app, machine, "Retired", "Deleted stopped Machine using recorded retirement proof")
                continue
            if machine["state"] != "started":
                report.record(app, machine, "Retained", "Machine is not started; cannot verify an empty process "
                              "and no usable stopped-Machine retirement proof")
                continue
            status = admin.status(endpoint)
            if status.get("deploymentId") != tags.get("purr_deployment") or not status.get("instanceId"):
                report.record(app, machine, "Needs review", "Deployment identity mismatch or missing process identity", status)
                continue
            if superseded and status.get("draining") is not True:
                status = admin.mutate(endpoint, "drain", status["instanceId"])
                if status.get("draining") is True:
                    fly.tag(app, machine["id"], "purr_phase", "draining")
            if status.get("draining") is not True:
                report.record(app, machine, "Retained", "Process has not confirmed draining", status)
                continue
            if status.get("canRetire") is not True:
                report.record(app, machine, "Retained", "Draining: process has not confirmed it can retire; "
                              "remaining activity or incomplete retirement status", status)
                continue
            retired = admin.mutate(endpoint, "retire", status["instanceId"])
            if (retired.get("retired") is not True or retired.get("instanceId") != status["instanceId"] or
                    retired.get("canRetire") is not True):
                raise DeploymentError("Relay did not confirm atomic retirement")
            fly.tag(app, machine["id"], "purr_retirement", status["instanceId"])
            fly.tag(app, machine["id"], "purr_phase", "retired")
            machine = fly.get(app, machine["id"])
            fly.stop_destroy(app, machine, admin)
            report.record(app, machine, "Retired", "Empty process atomically retired; Machine deletion confirmed", status)
        except MachineRetiredError as error:
            report.record(app, machine, "Retired", str(error), status)
        except DeploymentError as error:
            report.record(app, machine, "Needs review", f"Cleanup could not confirm completion; retry on the next run: {error}", status)
    report.apps[app] = f"Checked all {len(machines)} machines"


def prune_balancers(fly, admin, app, open_tunnel=tunnel, report=None):
    report = report if report is not None else CleanupReport()
    candidates = []
    machines = sorted(fly.machines(app), key=lambda m: m.get("created_at", ""))
    report.track(app, machines, "balancer")
    pending = []
    for machine in machines:
        if not owned(machine, "balancer"):
            report.record(app, machine, "Retained", "Legacy/unmanaged balancer: may be a directory dependency; "
                          "requires a separate one-time review")
        elif metadata(machine).get("purr_phase") not in ("draining", "retired"):
            report.record(app, machine, "Retained", "Balancer phase is not draining/retired; preserve the active "
                          "or unfinished generation")
        else:
            pending.append(machine)

    def retain_chain(blocker, reason):
        for candidate in pending:
            detail = reason if candidate["id"] == blocker["id"] else (
                f"Balancer chain retained because {blocker['id']} could not be verified: {reason}")
            report.record(app, candidate, "Needs review", detail)
        report.apps[app] = "Balancer chain blocked; no balancers deleted"

    # Resolve all forwarding chains before deleting any intermediate predecessor.
    for machine in pending:
        tags = metadata(machine)
        try:
            if machine["state"] == "stopped" and tags.get("purr_retirement"):
                candidates.append((machine, None))
                continue
            if machine["state"] != "started":
                retain_chain(machine, "Machine is not started and has no usable stopped-Machine retirement proof")
                return
            with open_tunnel(app, machine["id"]) as endpoint:
                status = admin.status(endpoint, host=private_host(app, machine["id"]))
            if status.get("canRetire") is not True or not status.get("successorUrl") or not status.get("instanceId"):
                retain_chain(machine, "Dependency or incomplete retirement proof: successor, process identity "
                             "and canRetire=true are required")
                return
            candidates.append((machine, status))
        except DeploymentError as error:
            retain_chain(machine, f"Unable to verify the balancer forwarding chain: {error}")
            return
    for machine, status in candidates:
        try:
            if status is None:
                fly.stop_destroy(app, machine, admin, open_tunnel)
                report.record(app, machine, "Retired", "Deleted stopped balancer using recorded retirement proof")
                continue
            fly.tag(app, machine["id"], "purr_retirement", status["instanceId"])
            fly.tag(app, machine["id"], "purr_phase", "retired")
            fly.stop_destroy(app, fly.get(app, machine["id"]), admin, open_tunnel)
            report.record(app, machine, "Retired", "Successor and retirement proof verified; Machine and owned volume cleanup confirmed")
        except MachineRetiredError as error:
            report.record(app, machine, "Retired", str(error))
        except DeploymentError as error:
            report.record(app, machine, "Needs review", f"Cleanup could not confirm completion; retry on the next run: {error}")
    report.apps[app] = f"Checked all {len(machines)} machines"


def cleanup(fly, admin, args):
    apps = [f"{args.app}-{prefix}" for _, prefix in REGIONS] + [args.app]
    report = CleanupReport(apps)
    current_app = apps[0]
    try:
        for current_app in apps[:-1]:
            prune_relays(fly, admin, current_app, f"https://{args.app}.{args.domain}", report)
        current_app = args.app
        prune_balancers(fly, admin, current_app, report=report)
    except Exception as error:
        report.apps[current_app] = report.text(f"Inspection incomplete: {error}")
        raise
    finally:
        report.write(getattr(args, "report_dir", None))


def run_json(*args):
    return json.loads(subprocess.check_output(args, text=True))


def ensure_network(args, fly):
    # Existing VIPs/custom domains are part of the client compatibility contract.
    fly.call("GET", f"/apps/{args.app}")
    ips = run_json("flyctl", "ips", "list", "--app", args.app, "--json")
    v4_types = ("v4",) if args.udp else ("v4", "shared_v4")
    if not any(ip.get("Type") in v4_types for ip in ips):
        command = ["flyctl", "ips", "allocate-v4", "--app", args.app, "--yes"]
        if not args.udp:
            command.append("--shared")
        subprocess.run(command, check=True)
        ips = run_json("flyctl", "ips", "list", "--app", args.app, "--json")
    addresses = [ip["Address"] for ip in ips if ip.get("Type") in v4_types]
    if not addresses:
        raise DeploymentError("No suitable IPv4 available")
    certificates = fly.call("GET", f"/apps/{args.app}/certificates?filter={args.domain}")
    if not any(cert.get("hostname") == args.domain for cert in certificates["certificates"]):
        fly.call("POST", f"/apps/{args.app}/certificates/acme", {"hostname": args.domain})
    cf_headers = {"Authorization": "Bearer " + os.environ["CLOUDFLARE_API_TOKEN"]}
    cf_base = "https://api.cloudflare.com/client/v4/zones/" + os.environ["CLOUDFLARE_ZONE_ID"] + "/dns_records"
    for record_type, address_type in (("A", "v4"), ("AAAA", "v6")):
        desired = (set(addresses) if record_type == "A" else
                   {ip["Address"] for ip in ips if ip.get("Type") == address_type})
        if not desired:
            continue
        query = urllib.parse.urlencode({"type": record_type, "name": args.domain})
        response = request("GET", cf_base + "?" + query, headers=cf_headers)
        if not response.get("success"):
            raise DeploymentError("Cloudflare DNS lookup failed")
        existing = response["result"]
        if any(r.get("content") not in desired or r.get("proxied") for r in existing):
            raise DeploymentError(f"Existing {record_type} DNS differs from this app; refusing to rewrite {args.domain}")
        for address in desired - {r["content"] for r in existing}:
            result = request("POST", cf_base, {"type": record_type, "name": args.domain,
                                              "content": address, "ttl": 120, "proxied": False}, cf_headers)
            if not result.get("success"):
                raise DeploymentError("Cloudflare DNS creation failed")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write("public_ip=" + ",".join(addresses) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for command in ("network", "balancer", "relay", "cleanup"):
        child = sub.add_parser(command)
        child.add_argument("--app", required=True)
        if command == "network":
            child.add_argument("--domain", required=True)
            child.add_argument("--udp", action="store_true")
        if command in ("relay", "balancer"):
            child.add_argument("--deployment", required=True)
            child.add_argument("--region", required=True)
            child.add_argument("--image", required=True)
        if command == "relay":
            child.add_argument("--prefix", required=True)
            child.add_argument("--domain", required=True)
            child.add_argument("--public-ip", required=True)
            child.add_argument("--balancer", required=True)
        if command == "balancer":
            child.add_argument("--public-url", required=True)
            child.add_argument("--initial-legacy-cutover", action="store_true",
                               help="One-time legacy balancer replacement without transferring its room directory")
        if command == "cleanup":
            child.add_argument("--domain", required=True)
            child.add_argument("--report-dir", help="Write Markdown and JSON drain reports in this directory")
    args = parser.parse_args()
    if hasattr(args, "deployment") and not re.fullmatch(r"[a-z0-9-]{1,35}", args.deployment):
        parser.error("deployment must contain 1–35 lowercase letters, digits or hyphens")
    args.secret = os.environ["APP_SECRET"]
    fly, admin = Fly(os.environ["FLY_API_TOKEN"]), Admin(args.secret)
    if args.command == "network":
        ensure_network(args, fly)
    elif args.command == "balancer":
        deploy_balancer(fly, admin, args)
    elif args.command == "relay":
        deploy_relay(fly, admin, args)
    else:
        cleanup(fly, admin, args)


if __name__ == "__main__":
    try:
        main()
    except (DeploymentError, subprocess.CalledProcessError) as error:
        print(f"Operation stopped without forced cleanup: {error}", file=sys.stderr)
        sys.exit(1)

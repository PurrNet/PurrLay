"""Verify the production Linux image's IPv4/IPv6 listeners and private handoff.

Run after building the balancer image:
  python PurrLay.Tests/balancer_ipv6_smoke.py --image purrbalancer-ipv6-verification:local

Uses only temporary Docker containers/network; no Fly credentials or mutations.
"""

import argparse
import json
import subprocess
import time
import urllib.error
import urllib.request
import uuid


def docker(*args):
    result = subprocess.run(["docker", *args], capture_output=True, text=True, timeout=60)
    if result.returncode:
        raise RuntimeError(f"docker {' '.join(args[:3])}: {result.stderr.strip()}")
    return (result.stdout + result.stderr if args[0] == "logs" else result.stdout).strip()


def request(url, path, body=None, headers=None):
    data = json.dumps(body).encode() if body is not None else None
    values = {"internal_key_secret": "PURRNET", **(headers or {})}
    if data is not None:
        values["Content-Type"] = "application/json"
    with urllib.request.urlopen(urllib.request.Request(url + path, data, values), timeout=3) as response:
        return json.load(response)


def wait_ready(url):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        try:
            status = request(url, "/admin/status")
            if status["ready"]:
                return status
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            pass
        time.sleep(0.1)
    raise AssertionError(f"Balancer did not become ready: {url}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--probe-image", default="golang:1.26", help="Existing Linux image containing curl")
    args = parser.parse_args()
    suffix = uuid.uuid4().hex[:12]
    network = "purr-ipv6-" + suffix
    app = "binding-" + suffix
    prefix = f"fd00:{suffix[:4]}:{suffix[4:8]}:{suffix[8:]}::"
    addresses = {"source": prefix + "2", "successor": prefix + "3"}
    hosts = {name: f"{name}.vm.{app}.internal" for name in addresses}
    containers = []
    docker("network", "create", "--ipv6", "--subnet", prefix + "/64", network)

    def start(name, predecessor=None, ipv4_only=False, delayed_dns=False):
        container = network + "-" + name
        containers.append(container)
        command = ["run", "--detach", "--name", container, "--network", network,
                   "--publish", "127.0.0.1::8080", "--env", "HOST=+", "--env", "PORT=8080",
                   "--env", "SECRET=PURRNET", "--env", f"FLY_MACHINE_ID={name}",
                   "--env", f"FLY_APP_NAME={app}", "--env", "BALANCER_STATE_PATH=/tmp/state.json"]
        if name in addresses:
            command += ["--ip6", addresses[name]]
        for alias, address in addresses.items():
            if delayed_dns and alias == name:
                continue
            command += ["--add-host", f"{hosts[alias]}={address}"]
        if ipv4_only:
            command += ["--add-host", f"{name}.vm.{app}.internal=127.0.0.1"]
        if predecessor:
            command += ["--env", f"BALANCER_PREDECESSOR_URL=http://{hosts[predecessor]}:8080"]
        docker(*command, args.image)
        if ipv4_only:
            return container, None
        binding = docker("port", container, "8080/tcp").splitlines()[0]
        return container, "http://" + binding

    def private(name, path):
        output = docker("run", "--rm", "--network", network, "--entrypoint", "curl", args.probe_image,
                        "--noproxy", "*", "--silent", "--show-error", "--fail", "--max-time", "5",
                        "--resolve", f"{hosts[name]}:8080:[{addresses[name]}]",
                        "--header", "internal_key_secret: PURRNET", f"http://{hosts[name]}:8080{path}")
        return json.loads(output)

    try:
        source, public_source = start("source", delayed_dns=True)
        time.sleep(0.5)
        try:
            request(public_source, "/admin/status")
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            pass
        else:
            raise AssertionError("Public listener started before private IPv6 DNS was available")
        subprocess.run(["docker", "exec", "-i", source, "tee", "-a", "/etc/hosts"],
                       input=f"{addresses['source']} {hosts['source']}\n", text=True,
                       capture_output=True, timeout=5, check=True)
        initial = wait_ready(public_source)
        assert private("source", "/admin/status")["instanceId"] == initial["instanceId"]
        request(public_source, "/registerServer", {
            "apiEndpoint": "http://127.0.0.1:65534", "region": "test", "host": "cached-host",
            "instanceId": "relay-instance", "deploymentId": "release",
        })
        room_headers = {"name": "preserved-room", "region": "test", "relay_endpoint": "http://127.0.0.1:65534",
                        "relay_instance_id": "relay-instance", "room_instance_id": "room-instance"}
        request(public_source, "/registerRoom", headers=room_headers)
        request(public_source, "/updateConnectionCount", headers={**room_headers, "count": "4", "count_sequence": "1"})
        assert private("source", "/list")["results"][0]["connectedPlayers"] == 4

        successor, public_successor = start("successor", "source")
        active = wait_ready(public_successor)
        assert active["instanceId"] != initial["instanceId"]
        assert private("successor", "/admin/status")["instanceId"] == active["instanceId"]
        assert private("successor", "/list")["results"][0]["name"] == "preserved-room"
        retired = request(public_source, "/admin/status")
        assert retired["ready"] is False
        assert retired["successorUrl"] == f"http://{hosts['successor']}:8080"
        request(public_source, "/updateConnectionCount", headers={**room_headers, "count": "7", "count_sequence": "2"})
        assert private("successor", "/list")["results"][0]["connectedPlayers"] == 7
        assert request(public_source, "/list") == request(public_successor, "/list")

        docker("stop", source)
        docker("kill", successor)
        docker("start", successor)
        public_successor = "http://" + docker("port", successor, "8080/tcp").splitlines()[0]
        restarted = wait_ready(public_successor)
        assert restarted["instanceId"] == active["instanceId"]
        assert private("successor", "/admin/status")["ready"] is True
        assert private("successor", "/list")["results"][0]["connectedPlayers"] == 7

        invalid, _ = start("invalid", ipv4_only=True)
        deadline = time.monotonic() + 5
        while docker("inspect", "--format", "{{.State.Running}}", invalid) == "true" and time.monotonic() < deadline:
            time.sleep(0.1)
        assert docker("inspect", "--format", "{{.State.ExitCode}}", invalid) != "0"
        assert "must resolve only to IPv6" in docker("logs", invalid)
        print("PASS: delayed DNS, shared IPv4/IPv6 state, private handoff/forwarding, source-dead restart, IPv4 binding guard")
    except Exception:
        for container in containers:
            result = subprocess.run(["docker", "logs", container], capture_output=True, text=True)
            print(f"{container}:\n{result.stdout}\n{result.stderr}")
        raise
    finally:
        for container in reversed(containers):
            subprocess.run(["docker", "rm", "--force", container], capture_output=True)
        subprocess.run(["docker", "network", "rm", network], capture_output=True)


if __name__ == "__main__":
    main()

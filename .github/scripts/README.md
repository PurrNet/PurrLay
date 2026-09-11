# Deploying generations on Fly

For the first migration from the existing IPv4-only legacy balancer, select
**initial_legacy_cutover** in **Deploy Purr Transport**. This explicit one-time
mode starts a fresh room directory without transferring the old directory.
Existing regional relay processes and their live transport sessions keep running;
old room discovery and reconnect information is lost. The old balancer is stopped
and retained after its replacement is ready. Leave this option disabled for later
deployments, which require a successful private IPv6 state handoff.

This option does not repair a failed deployment or change which tests run. If a
run fails in the test steps, fix that failure and retry with the option disabled.
Once managed generations exist, enabling it is rejected; existing Machines do not
need to be removed to make a normal deployment work.

Run **Deploy Purr Transport** from GitHub Actions. It runs the .NET session/recovery tests, Python deployment tests, and Linux IPv6 image smoke test before any Fly operations, then builds images, creates unmanaged Machines, hands off the balancer registry, and directs new rooms to the new relay generation. It does not update existing relay Machines or change their IP addresses. **Retire drained Purr Transport generations** runs every five minutes and can also be run manually. Both workflows share a concurrency group and never cancel an in-progress deployment or cleanup. `queue: max` also keeps scheduled cleanup from replacing a pending deployment in GitHub's default single-entry queue.

The existing `purrtransport` app and its seven regional apps must already exist in the same Fly organization/private network. The workflows reuse `FLY_API_TOKEN`, `PURR_APP_SECRET`, `CLOUDFLARE_API_TOKEN`, and `CLOUDFLARE_ZONE_ID`. The token needs Machine, volume, certificate, IP and image-registry permissions for these apps, plus private-network access for `flyctl proxy`. Missing apps or conflicting DNS fail closed; the script does not choose an organization or repoint an existing hostname.

## Stable hostnames, separate ports

Old clients can cache regional hostnames. Every generation therefore stays in its existing regional app and uses the existing custom hostname and dedicated IPv4. The allocator reserves a free five-port block between 20000 and 59999, including all services on stopped Machines and all port ranges. Incomplete Machine configuration prevents allocation.

The `china` app now deploys in Singapore (`sin`), where its existing relay runs.
Fly [retired Hong Kong (`hkg`)](https://fly.io/blog/the-region-consolidation-project/)
and rejects new Machines there. The app name, hostname and client region identifier
remain `china` for compatibility.

| External port | Internal port | Service |
| --- | --- | --- |
| base | 8081 | Relay HTTPS API; Fly terminates TLS and HTTP |
| base + 1 | same | Secure WebSocket; Fly terminates TLS |
| base + 2 | same | LiteNetLib V1 UDP |
| base + 3 | same | LiteNetLib V2 UDP |
| base + 4 | same | WebRTC UDP |

The balancer keeps room ownership pinned to each generation's exact endpoint. Region pings go directly to the regional HTTPS API port so region selection measures the player's path to the relay. WebRTC offer requests use the balancer's public HTTPS proxy on port 443. Existing WebSockets, UDP sessions and WebRTC connections continue on their original Machine. Old relays remain reachable for joins, reconnects and migration until retirement.

Machines have autostop disabled and do not carry Fly Launch's `fly_platform_version` metadata. The workflows only use `flyctl deploy --build-only --push`; they never use a deployment strategy, scale count, or Machine update to replace live relays. Do not use ordinary `fly deploy` or `fly scale count` against the production apps while rooms are running.

Images build and upload through Docker on the GitHub runner (`--local-only`),
avoiding the remote builder upload path that stalled the earlier rollout. Each
build/push step has a 15-minute timeout. A failed or timed-out upload prevents
that job from creating a new generation; rerunning reconciles any generation
already created by an earlier attempt.

## Balancer handoff and recovery

The balancer listens on both public IPv4 and its exact private IPv6 hostname, sharing the same directory. Startup waits for private IPv6 DNS before opening either listener. The successor starts cordoned. It contacts the predecessor through the exact `<machine-id>.vm.<app>.internal:8080` address, receives the registry, and completes a fenced handoff before reporting ready. The workflow probes this exact Machine using `flyctl proxy` with the remote Machine hostname and port in its HTTP `Host` header, uncordons it, verifies public routing to its process identity, and then cordons the predecessor. Cordoned predecessors remain accessible over the private network. The successor stays marked `standby` until predecessor finalization is complete, so interrupted Actions finish that work before creating another generation.

The controller waits for a newly created or restarted balancer to reach Fly's
`started` state before opening its private proxy. If private DNS is still
propagating and the proxy exits, it starts a fresh proxy within a bounded startup
deadline. Exhausting that deadline retains the candidate and its volume for a
retry; it does not replace the candidate or stop the predecessor.

Each new balancer generation has a dedicated encrypted 1 GB Fly volume mounted at `/data`; `BALANCER_STATE_PATH` stores its checkpoint there. A process restart uses that checkpoint. A retry for an already-created generation reuses its Machine and attached volume; an uncertain volume or Machine create is reconciled by its deterministic generation name before another mutation is considered.

The explicit initial cutover requires exactly one started unowned balancer and no owned generation, including stopped Machines. It verifies that exact public Machine returns a missing admin API and a valid, unambiguous relay registry. It records the cutover mode, predecessor ID and image on the candidate before creation. Only after the new private and public probes pass does it cordon and stop that exact unchanged legacy balancer. There is no legacy directory dependency in the new checkpoint. Old relays register with the new balancer on their next heartbeat, so its regional list can briefly be empty. New rooms then use the new directory while the regional rollout replaces their allocation targets.

An interrupted initial cutover resumes its original Machine and volume from recorded metadata even if the next Action leaves the option disabled. A lost stop acknowledgement is reconciled against the retained legacy Machine. No private-connectivity error or failed handoff ever enables this mode automatically. The legacy relay Machines stay running and remain excluded from automatic cleanup because they have no supported retirement protocol. If the normal handoff path encounters a reachable legacy predecessor instead, it retains that source as a private directory dependency; such dependencies must remain running.

## Retirement and failed runs

Open a **Retire drained Purr Transport generations** run's **Summary** to see the
drain report. It lists every inventoried Machine, its generation, state and phase
at the start of cleanup, whether it was retired or kept, and the reason. Draining
relays also show the last checked room, reservation, transport connection, pipe
connection and pending offer counts. `?` means the counter was unavailable;
active and legacy Machines are not probed for these counters.

The same report is printed in the log and saved as Markdown and JSON in the
**purrtransport-drain-report** artifact for seven days. Reports are written even
when cleanup raises an error, with unvisited apps and Machines marked as not
checked. A runner being forcibly terminated may prevent the final report write.
A successful workflow can keep Machines; **Needs review** identifies API failures
or missing proof, including rate limits that can be retried on the next run.

Two Machines in a region commonly mean an active generation plus a draining or
legacy predecessor. Managed predecessors retire automatically once empty. Legacy
Machines lack the supported retirement protocol and are retained indefinitely,
even when idle; the report explicitly calls out this one-time review requirement.
They must not be assumed empty from their age or the newer Machine's presence.
If Machine deletion succeeds but owned-volume cleanup fails, the report records
the Machine as retired and identifies the volume requiring inspection.

Cleanup requires a healthy authoritative balancer. It excludes active relay endpoints and unowned Machines. If deployment stopped after central activation but before draining the previous relay, cleanup can finish draining only when the registry explicitly identifies that endpoint and deployment as superseded. An absent registry entry is not proof of supersession.

For a draining relay, `/admin/retire` must atomically acknowledge the exact process identity with no rooms, reservations, transport connections, pipe connections or pending offers. The script records that acknowledgement in Machine metadata. Immediately before stopping a running Machine, it verifies the same sealed process again. A changed process or failed status request is retained. Stop/delete retries can resume from recorded retirement after the Machine has stopped.

Balancer cleanup first resolves every retained forwarding chain before deleting any intermediate predecessor. It requires explicit retirement proof and preserves every legacy dependency. An owned generation volume is deleted only after its exact retired Machine has been deleted and the volume is confirmed detached. Legacy and shared volumes are never removed. An interruption between Machine deletion and volume deletion can leave a detached volume for manual inspection; it is not guessed safe from its name alone.

Old generations may remain for hours or across several releases. There is no maximum drain age and no forced session expiry. They continue to incur normal running-Machine and volume charges until safely retired. Scheduled Actions may be delayed; this only delays cleanup. Failed or ambiguous generations and their volumes remain available for diagnosis instead of being automatically destroyed.

Generation identity is `${github.run_id}-${github.run_attempt}`. Before a retry creates a fresh balancer generation, it resumes an unfinished standby balancer using that Machine's original image, identity and volume. An ambiguous set of unfinished Machines blocks deployment and prints their IDs for recovery. Re-invoking the script with the same identity reuses the existing generation. Never reuse an identity for a different image. Workflow logs identify active, draining and retained Machines. Admin endpoints require the existing internal secret, except the public readiness probe; relay mutation calls also carry the current process identity.

## Local verification

The tests use a mock Machines API and make no Fly changes:

```sh
python3 -m unittest discover -s .github/scripts -p 'test_*.py' -v
```

The Linux image smoke test checks shared IPv4/IPv6 state, private handoff and
forwarding, restart from a checkpoint with the predecessor stopped, delayed DNS
recovery, and rejection of an IPv4-only private hostname.
It creates and removes temporary Docker containers and a private Docker network:

```sh
docker build --file PurrBalancer/dockerfile -t purrbalancer-ipv6-verification:local PurrBalancer
docker pull golang:1.26
python3 PurrLay.Tests/balancer_ipv6_smoke.py --image purrbalancer-ipv6-verification:local
```

The networking and lifecycle shapes follow Fly's [Machines API](https://fly.io/docs/machines/api/machines-resource/), [volume API](https://fly.io/docs/machines/api/volumes-resource/), [private proxy CLI](https://fly.io/docs/flyctl/proxy/), and [UDP requirements](https://fly.io/docs/networking/udp-and-tcp/). Mixed Machine services in one app route by the requested external port; UDP requires equal internal and external ports.

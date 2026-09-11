WIP

**PurrBalancer** registers the rooms and lets user know of all servers

**PurrLay** is the relay and handles the actual game and connections

Room names can be reused after a room becomes empty. The relay registers each
replacement with a new internal instance ID before returning connection details.
Delayed player-count or unregister requests from a previous instance cannot change
the replacement's route. These IDs do not change public room names or the client
protocol.

Deploy the updated **PurrBalancer before PurrLay**; both updates are required for
the complete fix. For updated relays, `EMPTY_ROOM_TIMEOUT_SECONDS` (default: 300)
is enforced by PurrLay, which checks every 30 seconds and unregisters the matching
instance. Failed unregister requests are retried on subsequent cleanup checks.
PurrBalancer retains independent empty-room cleanup for older relays and removes
routes when a relay fails its health check. A relay restart at the same endpoint
also clears routes owned by the previous process.

Run the room lifecycle regression tests with `dotnet test PurrLay.sln`.

## Deployments that preserve rooms

For the first production rollout, enable **initial_legacy_cutover** in the
**Deploy Purr Transport** GitHub Action. The existing IPv4-only balancer cannot
transfer its directory over Fly's private IPv6 network, so this explicit one-time
cutover starts a fresh directory. Existing regional relays and their live
connections keep running, but old room discovery and reconnect information is
lost. Leave the option disabled for subsequent deployments.

The deployment workflow starts a new relay Machine alongside the current one.
After the replacement is ready, new rooms use it; existing rooms, late joins,
host migration, and established connections continue on their original relay.
Old relays remain reachable until they have no rooms, pending allocations,
negotiations, or transport connections. Retirement seals further admission before
the cleanup workflow stops the Machine. A failed status request is never treated
as evidence that a relay is empty, and there is no deadline that forcibly ends
long matches.

Each regional app keeps its existing hostname and public addresses. Every relay
generation has separate service ports, returned through the existing allocation
and join responses. This also supports older clients that cache the regional
hostname. Region checks contact the relay directly so they measure the player's
path to that region. WebRTC signaling uses the balancer's existing HTTPS endpoint,
which forwards each offer to the correct relay generation.

Balancer updates transfer the complete room directory and ownership records to
the successor. Once the transfer commits, the predecessor forwards subsequent
requests to that successor. The replacement becomes ready only after it has the
state needed to serve requests. Each new balancer keeps a durable checkpoint on
its own encrypted 1 GB Fly volume so a restart can restore that directory.
New rooms and old rooms retain their respective owners during the transition.

The initial cutover stops and retains the old balancer only after its replacement
is ready. Later deployments require a successful state transfer; a failed handoff
never starts a fresh directory. Legacy regional relays remain running because
their old connection count does not prove that every session has ended. They are
not automatically destroyed. New generations support automatic retirement;
retained generations incur their normal Machine costs while they remain running.

Use the deployment and cleanup workflows to manage these Machines. They create
and retire individual generations; running an ordinary `fly deploy` or scaling a
regional app down to one Machine bypasses this lifecycle and can interrupt rooms.
Existing GitHub secrets are reused. The rollout changes require no game-client
update. This preserves sessions during planned deployments; it does not transfer
live sessions away from a crashed relay.

See the [deployment operations guide](.github/scripts/README.md) for retry,
cleanup, and recovery details.

## Optional browser WebRTC

PurrLay can terminate browser WebRTC data channels and forward them to existing
native LiteNetLib connections. Native clients retain UDP. Browsers can fall back
to WebSockets when the relay does not advertise WebRTC or negotiation fails.
The gateway is a local Go/Pion process supervised by PurrLay. It uses a single
shared UDP port for ICE rather than allocating a public port per player.

Build the gateway in `PurrLay/PurrLay.WebRtcGateway` with
`go build -o ../bin/Debug/net8.0/PurrLay.WebRtcGateway .` (add `.exe` on Windows),
or set `WEBRTC_GATEWAY_PATH` to its absolute executable path. The Docker build
includes the executable and enables it by default. Local .NET runs require
`WEBRTC_ENABLED=true`. No gateway binary or `WEBRTC_ENABLED=false` leaves the
existing WebSocket and native UDP behavior available.

| Setting | Default | Purpose |
| --- | --- | --- |
| `WEBRTC_ENABLED` | `false`; `true` in Docker | Start the supervised gateway. |
| `WEBRTC_GATEWAY_PATH` | `PurrLay.WebRtcGateway` beside PurrLay, `.exe` on Windows | Gateway executable. |
| `WEBRTC_UDP_PORT` | `7779` | Public ICE/DTLS/SCTP UDP port. |
| `WEBRTC_BIND_ADDRESS` | Gateway default; `fly-global-services` IPv4 on Fly | Local address owning the shared UDP socket. |
| `WEBRTC_PUBLIC_IP` | Unset | Comma-separated public IPv4 addresses advertised as ICE host candidates. Required on Fly. |
| `WEBRTC_MAX_SESSIONS` | `1024` | Maximum gateway/bridge sessions. |
| `WEBRTC_HTTP_ADDRESS` | `127.0.0.1:8090` | Private gateway HTTP listener; must be loopback. |
| `PURR_WEBRTC_BRIDGE_ADDRESS` | `127.0.0.1:8091` | Private framed TCP bridge; must be loopback. |

PurrLay creates a random `PURR_WEBRTC_BRIDGE_TOKEN` only in the child process
environment. Do not persist or configure this token. Neither private listener
should be published by Docker, Fly, or a reverse proxy.

The GitHub Actions deployment workflow handles WebRTC configuration automatically:
it ensures each regional relay has a dedicated IPv4 before deployment and passes
that address as `WEBRTC_PUBLIC_IP`. The Docker image builds and enables the
gateway. Generation deployments assign its UDP port alongside the other service
ports; `PurrLay/fly.toml` retains UDP `7779` for manual deployments. No additional GitHub secrets or
manual per-region settings are required. Push these changes before running the
workflow, and rebuild browser games with the updated PurrTransport to use WebRTC.

For manual Fly deployments, assign the app a **dedicated public IPv4**, set
`WEBRTC_PUBLIC_IP` to that address, and expose UDP `7779` as shown in
`PurrLay/fly.toml`. A shared HTTP IPv4
does not provide this UDP endpoint. The relay deliberately leaves WebRTC disabled
on Fly when `WEBRTC_PUBLIC_IP` is absent, since private machine candidates are not
usable by public browsers. For another deployment behind NAT or Docker port
mapping, advertise the reachable public IPv4 and forward the same UDP port to
the gateway. TLS terminates on the existing HTTPS relay API for signaling; the
WebRTC data connection negotiates its own DTLS encryption.

Allocation, join, and migration details include `webRtcUrl` only after the
gateway is healthy. Generation deployments use the balancer's HTTPS endpoint at
`/relay/{instanceId}/webrtc/offer`; manual deployments use the relay API's
`/webrtc/offer`. The endpoint accepts `POST` JSON `{ "type": "offer", "sdp": "..." }`
and returns a gathered SDP answer. Signaling bodies are limited to 128 KiB and
negotiation requests time out after 25 seconds. A gateway failure closes its
active browser connections and removes the advertised capability. PurrLay
automatically restarts the gateway, retrying after 2 seconds and doubling the
delay up to 30 seconds while failures continue. After 60 seconds of healthy
operation the retry delay resets. Transient startup failures are retried too.
WebSockets remain available during recovery, and WebRTC is advertised again
only after the replacement is healthy. Existing connections do not migrate to
a new transport mid-session; affected players use the game's reconnect or
host-migration handling. Relay shutdown cancels retries and stops the gateway.
The gateway also exits if its owning relay process terminates unexpectedly.

Data-channel delivery metadata is preserved through the relay. Reliable control
messages and authentication use delivery method 2. Bridge messages have a maximum
payload of 65,535 bytes (65,531 for sequenced data, which includes a four-byte
sequence counter on the browser connection); each peer has bounded outgoing buffering, drops excess
unreliable messages, and disconnects if reliable traffic cannot be queued. This
backend does not enable browser-to-native LiteNetLib NAT punching or TURN service.
Browsers on networks that block the UDP path use the WebSocket fallback.

Run `dotnet test PurrLay.sln` for room, migration, mixed-transport framing,
bridge lifecycle/authentication, and signaling validation. Run `go test ./...`
in the gateway directory for its transport tests. Build the Docker image from
the `PurrLay` directory, matching the existing Fly build context. Configuration
changes require deployment; editing these files does not deploy the service.

The .NET suite also launches a local test child to verify automatic restart,
health-check failures, port conflicts, interrupted offers and shutdown cleanup.
It does not need a Go installation or a deployed relay.

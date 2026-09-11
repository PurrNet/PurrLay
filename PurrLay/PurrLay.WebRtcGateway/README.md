# PurrLay WebRTC gateway

This companion process terminates browser WebRTC data channels and forwards
channel-tagged messages to the existing .NET PurrLay room/router process. It
uses Pion WebRTC **v4.2.20**, including its SCTP partial-reliability implementation.
Native clients keep their existing LiteNetLib transport. This is a browser-to-relay
transport; it does not introduce direct player-to-player connections or a TURN server.

## Build and verify

Go 1.24 or newer is required by the pinned dependency versions.

```sh
go mod download
go test ./...
go build -trimpath -o PurrLay.WebRtcGateway .
```

The Linux Docker image builds with `CGO_ENABLED=0`; no Go runtime or native WebRTC
libraries are required in the runtime image. On Windows use the `.exe` suffix.
PurrLay supervises this binary; enable the feature through the parent relay's
`WEBRTC_ENABLED` setting and place the executable at its configured gateway path.
PurrLay supplies an unpredictable bridge token through the child environment.

The integration tests perform real Pion ICE/DTLS/SCTP connections, exchange SDP
through the HTTP handler, authenticate a real TCP bridge, and exercise all four
delivery modes in both directions. They cover sequence filtering, maximum payloads,
mode 3 mapping, malformed channels, capacity, and disconnect cleanup. Additional
tests cover bounded queues and framing. On a supported C toolchain, also run
`go test -race ./...` before shipping changes to concurrency behavior.

## Process settings

| Variable | Default | Purpose |
|---|---|---|
| `WEBRTC_HTTP_ADDRESS` | `127.0.0.1:8090` | Private HTTP signaling listener; numeric loopback only. |
| `PURR_WEBRTC_BRIDGE_ADDRESS` | `127.0.0.1:8091` | Private TCP bridge listener in .NET; numeric loopback only. |
| `PURR_WEBRTC_BRIDGE_TOKEN` | Required | Per-child bridge authentication token, never logged. |
| `WEBRTC_UDP_PORT` | `7779` | Shared ICE/DTLS/SCTP UDP port for all browser peers. |
| `WEBRTC_BIND_ADDRESS` | `0.0.0.0`, or `fly-global-services` when `FLY_APP_NAME` is set | Local UDP bind address. |
| `WEBRTC_PUBLIC_IP` | Empty | Comma-separated public IPv4 addresses to advertise in ICE host candidates. Set this behind Fly's address translation. |
| `WEBRTC_MAX_SESSIONS` | `1024` | Combined established and pending peer limit. |

Invalid configuration or a failed listener bind exits with status 1. Interrupt
or SIGTERM shuts down signaling, active peers, TCP bridges, and the UDP mux.
Losing either a peer or its bridge closes the other side. All four expected data
channels must open within 20 seconds. No external STUN service is contacted;
the public relay advertises its configured reachable address directly.

## Signaling API

Only the parent relay should expose/proxy signaling publicly. The gateway binds
to loopback and intentionally has no public CORS or browser authentication policy.

- `GET /health` returns `{"status":"ok"}` while the process is running.
- `POST /offer` accepts `{"type":"offer","sdp":"..."}` and returns a fully gathered
  `{"type":"answer","sdp":"..."}`. Request and response limits are 128 KiB.

The browser must finish ICE gathering before submitting its offer, because this
endpoint does not accept subsequent trickle ICE candidates. Before returning an
answer, the gateway establishes and authenticates a dedicated TCP bridge. HTTP
setup has a 20-second timeout; the parent proxy should allow at least 25 seconds.
HTTP health only reports gateway liveness; an offer also verifies bridge access.

## Browser data channels

The browser creates these four binary channels before creating its offer:

| Label | Purr delivery method | `ordered` | `maxRetransmits` |
|---|---|---|---|
| `purr-0` | ReliableUnordered | `false` | Unset |
| `purr-1` | Sequenced | `false` | `0` |
| `purr-2` | ReliableOrdered | `true` | Unset |
| `purr-4` | Unreliable | `false` | `0` |

Keep `protocol` empty, `negotiated` false, and `maxPacketLifeTime` unset. Unknown,
duplicate, text, or incorrectly configured channels close the connection.
There is no channel byte prepended to ordinary data-channel messages.

`purr-1` messages prepend a **uint32 little-endian sequence number**. Each direction
has its own counter. The gateway removes this header before forwarding to .NET
and discards duplicate/older messages using wraparound-aware comparison. Outbound
messages receive a counter starting at zero. Other channel payloads pass unchanged.
Every data-channel message is limited to 65,535 bytes including its sequence
header; `purr-1` therefore accepts at most 65,531 payload bytes. The relay's
existing MTU/framing rules still apply.

SCTP zero retransmits does not eliminate send buffering. Both bridge directions
have bounded queues of 128 frames and 1 MiB; transient frames have a smaller
16-frame backlog and older snapshots are evicted first. Unreliable messages are
dropped if a data channel already has more than 64 KiB buffered. Reliable queue
or transport-buffer overflow closes the connection instead of silently losing
reliable data. Reliable channels share the SCTP association's congestion control.

## Private bridge protocol

One TCP connection corresponds to one browser peer. Every frame starts with:

```text
offset  bytes  meaning
0       1      type: 0 = Hello, 1 = Data, 2 = Close
1       1      delivery method
2       4      payload length, unsigned little-endian
6       N      payload, at most 65,535 bytes
```

1. Gateway sends Hello, delivery 0, UTF-8 token payload.
2. .NET validates it and returns Hello, delivery 0, empty payload.
3. Data frames carry delivery 0, 1, 2, or 4 with original Purr wire bytes.
4. .NET's internal delivery 3 maps to browser channel `purr-2`.
5. Close uses delivery 0 and an empty payload. EOF has the same teardown meaning.

The .NET bridge retains room membership, peer identifiers, routing, and Purr UDP
framing authority. The gateway must never be reachable as a public unauthenticated
bridge. Its frame reader checks lengths before allocation and writes have a
10-second deadline so a stalled relay cannot retain a peer indefinitely.

## Fly deployment

Fly requires a dedicated public IPv4 address for public UDP, a UDP socket bound
to `fly-global-services`, and the same external and internal UDP port. Add UDP
7779 to the service configuration and set `WEBRTC_PUBLIC_IP` to the address
allocated to the relay. Keep HTTP 8090 and bridge 8091 private to the Machine.
The public signaling request and subsequent UDP traffic must reach the same
relay Machine that owns the peer; preserve PurrLay's existing per-relay addressing
and do not independently load-balance those two steps across unrelated instances.

Sources: [Fly UDP networking](https://fly.io/docs/networking/udp-and-tcp/),
[Pion single-port example](https://github.com/pion/webrtc/blob/v4.2.20/examples/ice-single-port/main.go),
[Pion SCTP partial-reliability tests](https://github.com/pion/sctp/blob/v1.11.1/association_test.go).

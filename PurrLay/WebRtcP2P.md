# Optional direct WebRTC connections

PurrTransport's WebRTC P2P setting is off by default. When enabled on both the
host and joining client, compatible peers may carry all game traffic directly
over WebRTC while keeping their authenticated relay connections alive for room
membership, keepalives, and disconnect handling. Browsers use their built-in
WebRTC implementation. Native Unity peers require the optional Unity WebRTC
adapter; installing or enabling the relay alone does not add that native adapter.

The relay offers this handshake only to authenticated peers on UDP V2 or the
WebRTC gateway. WebSocket, UDP V1, pipe, and older clients keep their existing
protocol. When both native UDP V2 peers also request the existing NAT punching
path, that path takes priority. There is no new public signaling endpoint or
relay configuration switch.

## Negotiation and route selection

Authentication JSON adds `"webRtcP2P": true`. The relay starts an attempt only
between the current room host and a newly joining client, with a fresh opaque
token. Both peers receive an introduction before the host receives
`SERVER_CLIENT_CONNECTED` and before the client receives `SERVER_AUTHENTICATED`.
A client that was already authenticated when the host arrived stays relay-only.

Each peer reports `ready` only after all four direct data channels are open. The
relay commits direct routing only after both reports arrive. A failure, exhausted
attempt budget, or eight-second deadline commits relay routing instead. The relay
sends the same commit to both peers, then emits the normal game connection
notifications. No game packets are forwarded through the relay for a pending or
committed-direct client. A direct attempt can add up to eight seconds to joining
when network restrictions prevent it from succeeding.

The selected route stays fixed for that session. A direct connection that fails
after commitment causes a disconnect; the game must reconnect or run its existing
host-migration handling. Switching a reliable stream between paths mid-session
could lose or reorder packets. On host migration, the relay commits pending
attempts to relay and disconnects committed-direct clients so they cannot retain
the old host's route. Existing relay clients retain their normal migration path.

This protocol does not provide TURN. STUN configuration belongs to PurrTransport;
peers whose networks prevent direct connectivity fall back before gameplay starts.

## Wire format

All integers in existing transport envelopes remain little-endian. Signaling is
UTF-8 JSON sent with reliable ordered delivery, method `2`.

| Direction | Payload prefix | JSON fields |
| --- | --- | --- |
| Relay to either peer | Server packet `7` | `type`, `token`, `clientId`, `signal`, `direct` |
| Host to relay | Host packet `3` | `type`, `token`, optional `signal` |
| Client to relay | Payload byte `255` | `type`, `token`, optional `signal` |

The client prefix `255` is inside the reliable ordered message payload. It is not
the WebRTC bridge's delivery byte, which remains `2`. Relay message types are
`introduce`, `signal`, and `commit`; peer message types are `signal`, `ready`, and
`failed`. `signal` contains an opaque string understood by the peer implementation.
`clientId` always identifies the joining client's relay connection, including on
messages sent to that client. `direct` is meaningful on `commit`.

The token scopes messages to one authenticated host/client pair. The relay checks
current room membership and host identity on every forwarded message. Clients
cannot provide arbitrary destination IDs. Tokens expire when an attempt commits,
either peer disconnects, or the host migrates; subsequent signaling is ignored.

## Bounds

- Outer JSON: 48 KiB; decoded signal string: 32 KiB of UTF-8.
- Pending attempts: 32 per host and 1,024 per relay process.
- Each endpoint of an attempt: 96 controls and 256 KiB in total, allowing an SDP
  message plus 64 ICE candidates and readiness messages.
- Each connection: 256 controls and 2 MiB per second before JSON parsing.
- JSON nesting: eight levels. Malformed, oversized, unauthenticated, or unrelated
  messages are discarded. The authoritative deadline still bounds the attempt.

All handshake transitions and sends share the room coordination lock. Signals
cannot follow the authentication notification, after which a client's legacy
receive path interprets relay packets as unframed game data. Old clients and old
relays do not opt in, so their existing game framing remains unchanged.

Run `dotnet test PurrLay.Tests/PurrLay.Tests.csproj --filter
FullyQualifiedName~WebRtcP2PTests` from the repository root for relay protocol,
isolation, fallback, and lifecycle tests. End-to-end direct data-channel tests
also require the updated PurrTransport client.

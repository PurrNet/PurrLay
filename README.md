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

using System.Threading;
using PurrBalancer;

namespace PurrLay;

public class Room
{
    public string? name;
    public string? hostSecret;
    public string? clientSecret;
    public DateTime createdAt;
    public ulong roomId;
    public DateTime? emptySince; // When the room became empty (null if room has players)
}

public static class Lobby
{
    static readonly Dictionary<string, Room> _room = new();
    static readonly Dictionary<ulong, string> _roomIdToName = new();
    static readonly object _roomLock = new();

    static int _roomIdCounter;

    /// <summary>
    /// Executes an async task in a fire-and-forget manner, logging any exceptions.
    /// </summary>
    static void FireAndForget(Task task, string operationName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await task;
            }
            catch (Exception e)
            {
                await Console.Error.WriteLineAsync($"Error in fire-and-forget operation '{operationName}': {e.Message}\n{e.StackTrace}");
            }
        });
    }

    public static async Task<string> CreateRoom(string region, string name)
    {
        var hostSecret = Guid.NewGuid().ToString().Replace("-", "");

        lock (_roomLock)
        {
            if (_room.TryGetValue(name, out var existing))
            {
                if (Transport.TryGetRoomPlayerCount(existing.roomId, out var currentCount) && currentCount > 0)
                    throw new Exception("Room already exists");

                var now = DateTime.UtcNow;
                existing.hostSecret = hostSecret;
                existing.clientSecret = Guid.NewGuid().ToString().Replace("-", "");
                existing.createdAt = now;
                existing.emptySince = now; // Room is being reused but still empty, track from now
                return hostSecret;
            }
        }

        await HTTPRestAPI.RegisterRoom(region, name);

        Console.WriteLine($"Registered room {name}");
        
        lock (_roomLock)
        {
            var roomId = (ulong)Interlocked.Increment(ref _roomIdCounter) - 1;
            _roomIdToName.Add(roomId, name);
            var now = DateTime.UtcNow;
            _room.Add(name, new Room
            {
                name = name,
                hostSecret = hostSecret,
                clientSecret = Guid.NewGuid().ToString().Replace("-", ""),
                createdAt = now,
                roomId = roomId,
                emptySince = now // Room starts empty, track from creation time
            });
        }

        return hostSecret;
    }

    public static bool TryGetRoom(string name, out Room? room)
    {
        lock (_roomLock)
        {
            return _room.TryGetValue(name, out room);
        }
    }

    public static void UpdateRoomPlayerCount(ulong roomId, int newPlayerCount)
    {
        lock (_roomLock)
        {
            if (_roomIdToName.TryGetValue(roomId, out var name) && _room.TryGetValue(name, out var room))
            {
                FireAndForget(HTTPRestAPI.updateConnectionCount(name, newPlayerCount), $"updateConnectionCount for room '{name}'");
                
                // Track when room becomes empty
                if (newPlayerCount == 0)
                {
                    room.emptySince = DateTime.UtcNow;
                }
                else
                {
                    room.emptySince = null; // Room has players, clear empty timestamp
                }
            }
        }
    }

    public static void RemoveRoom(ulong roomId)
    {
        lock (_roomLock)
        {
            if (_roomIdToName.Remove(roomId, out var name))
            {
                _room.Remove(name);
                FireAndForget(HTTPRestAPI.unegisterRoom(name), $"unregisterRoom for room '{name}'");
            }
        }
    }

    public static async Task StartEmptyRoomCleanupTask()
    {
        // Default timeout: 5 minutes (300 seconds)
        // Can be configured via EMPTY_ROOM_TIMEOUT_SECONDS environment variable
        const int DEFAULT_TIMEOUT_SECONDS = 300;
        var timeoutSeconds = Env.TryGetIntOrDefault("EMPTY_ROOM_TIMEOUT_SECONDS", DEFAULT_TIMEOUT_SECONDS);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        
        // Check every 30 seconds
        const int CHECK_INTERVAL_SECONDS = 30;

        Console.WriteLine($"Empty room cleanup task started. Timeout: {timeoutSeconds} seconds, Check interval: {CHECK_INTERVAL_SECONDS} seconds");

        while (true)
        {
            try
            {
                await Task.Delay(CHECK_INTERVAL_SECONDS * 1000);

                var now = DateTime.UtcNow;
                var roomsToRemove = new List<(ulong roomId, string name, double emptyDuration)>();

                lock (_roomLock)
                {
                    foreach (var (name, room) in _room)
                    {
                        // Only check rooms that are empty
                        // emptySince == null means room has players, skip it
                        // emptySince.HasValue means room is empty (either never joined or all left)
                        if (!room.emptySince.HasValue)
                            continue; // Room has players, skip
                        
                        var emptyDuration = now - room.emptySince.Value;
                        if (emptyDuration >= timeout)
                        {
                            roomsToRemove.Add((room.roomId, name, emptyDuration.TotalSeconds));
                        }
                    }
                }

                // Remove timed-out rooms
                foreach (var (roomId, name, emptyDurationSeconds) in roomsToRemove)
                {
                    // Double-check: verify room is still empty and still exists before removing
                    // TryGetRoomPlayerCount returns false if room has no players (count == 0)
                    if (!Transport.TryGetRoomPlayerCount(roomId, out _))
                    {
                        // Verify room still exists and is still marked as empty
                        lock (_roomLock)
                        {
                            if (_roomIdToName.TryGetValue(roomId, out var roomName) && 
                                _room.TryGetValue(roomName, out var room) && 
                                room.emptySince.HasValue)
                            {
                                Console.WriteLine($"Removing empty room {roomName} (ID: {roomId}, empty for {emptyDurationSeconds:F0} seconds)");
                                RemoveRoom(roomId);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                await Console.Error.WriteLineAsync($"Error in empty room cleanup task: {e.Message}\n{e.StackTrace}");
            }
        }
    }
}

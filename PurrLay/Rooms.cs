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
    public int migrationGeneration;
    public string? migrationFencingToken;
    public string? migrationPromotedPlayerId;
    public DateTime? migrationClaimedAt;
    internal string instanceId = Guid.NewGuid().ToString("N");
    internal string? previousInstanceId;
    internal bool registered;
    internal bool registrationInProgress;
    internal bool removed;
    internal bool removalInProgress;
    internal long countSequence;
}

public readonly struct MigrationRoomSnapshot
{
    public readonly string? name;
    public readonly string? hostSecret;
    public readonly string? clientSecret;
    public readonly ulong roomId;
    public readonly int generation;
    public readonly string? fencingToken;
    public readonly string? promotedPlayerId;
    public readonly DateTime? claimedAt;

    public MigrationRoomSnapshot(Room room)
    {
        name = room.name;
        hostSecret = room.hostSecret;
        clientSecret = room.clientSecret;
        roomId = room.roomId;
        generation = room.migrationGeneration;
        fencingToken = room.migrationFencingToken;
        promotedPlayerId = room.migrationPromotedPlayerId;
        claimedAt = room.migrationClaimedAt;
    }
}

public static class Lobby
{
    static readonly Dictionary<string, Room> _room = new();
    static readonly Dictionary<ulong, string> _roomIdToName = new();
    internal static readonly object SyncRoot = new();
    static readonly object _roomLock = SyncRoot;

    static int _roomIdCounter;

    static string NewSecret() => Guid.NewGuid().ToString().Replace("-", "");

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
        Room room;
        lock (_roomLock)
        {
            if (_room.TryGetValue(name, out var existing))
            {
                if (existing.registrationInProgress)
                    throw new Exception("Room creation already in progress");

                if (Transport.TryGetRoomPlayerCount(existing.roomId, out var currentCount) && currentCount > 0)
                    throw new Exception("Room already exists");
            }

            if (existing != null && !existing.registered && !existing.removed)
            {
                room = existing;
            }
            else
            {
                room = new Room
                {
                    name = name,
                    hostSecret = NewSecret(),
                    clientSecret = NewSecret(),
                    createdAt = DateTime.UtcNow,
                    roomId = (ulong)Interlocked.Increment(ref _roomIdCounter) - 1,
                    emptySince = DateTime.UtcNow,
                    previousInstanceId = existing?.instanceId
                };
                if (existing != null)
                {
                    Transport.RemoveEmptyRoomState(existing.roomId);
                    _roomIdToName.Remove(existing.roomId);
                }
                _room[name] = room;
                _roomIdToName.Add(room.roomId, name);
            }
            room.registrationInProgress = true;
        }

        try
        {
            await HTTPRestAPI.RegisterRoom(region, name, room.instanceId, room.previousInstanceId);
            lock (_roomLock)
            {
                room.registered = true;
                room.registrationInProgress = false;
                room.createdAt = DateTime.UtcNow;
                room.emptySince = room.createdAt;
            }
            Console.WriteLine($"Registered room {name}");
            return room.hostSecret!;
        }
        catch
        {
            lock (_roomLock)
                room.registrationInProgress = false;
            throw;
        }
    }

    public static MigrationRoomSnapshot ClaimMigration(
        string name,
        string claimSecret,
        string? promotedPlayerId,
        int? expectedGeneration)
    {
        ulong roomId;
        MigrationRoomSnapshot snapshot;

        lock (_roomLock)
        {
            if (!_room.TryGetValue(name, out var room) || !room.registered)
                throw new Exception("Room not found");

            if (!string.Equals(room.clientSecret, claimSecret) && !string.Equals(room.hostSecret, claimSecret))
                throw new Exception("Invalid migration secret");

            if (expectedGeneration.HasValue && room.migrationGeneration != expectedGeneration.Value)
                throw new Exception($"Migration generation mismatch. Expected {expectedGeneration.Value}, current {room.migrationGeneration}");

            var now = DateTime.UtcNow;
            room.hostSecret = NewSecret();
            room.clientSecret = NewSecret();
            room.createdAt = now;
            room.emptySince = now;
            room.migrationGeneration++;
            room.migrationFencingToken = NewSecret();
            room.migrationPromotedPlayerId = promotedPlayerId;
            room.migrationClaimedAt = now;

            roomId = room.roomId;
            snapshot = new MigrationRoomSnapshot(room);

            Transport.ReleaseRoomHostForMigration(roomId);

            if (!Transport.TryGetRoomPlayerCount(roomId, out _))
                UpdateRoomPlayerCount(roomId, 0);
        }

        return snapshot;
    }

    public static bool TryGetRoom(string name, out Room? room)
    {
        lock (_roomLock)
        {
            if (_room.TryGetValue(name, out room) && room.registered)
                return true;
            room = null;
            return false;
        }
    }

    public static bool TryGetMigrationState(string name, out MigrationRoomSnapshot snapshot)
    {
        lock (_roomLock)
        {
            if (_room.TryGetValue(name, out var room) && room.registered)
            {
                snapshot = new MigrationRoomSnapshot(room);
                return true;
            }
        }

        snapshot = default;
        return false;
    }

    public static void UpdateRoomPlayerCount(ulong roomId, int newPlayerCount)
    {
        lock (_roomLock)
        {
            if (_roomIdToName.TryGetValue(roomId, out var name) && _room.TryGetValue(name, out var room) && room.registered)
            {
                Transport.TryGetRoomPlayerCount(roomId, out newPlayerCount);
                FireAndForget(HTTPRestAPI.updateConnectionCount(name, newPlayerCount, room.instanceId, ++room.countSequence),
                    $"updateConnectionCount for room '{name}'");

                // Track when room becomes empty
                if (newPlayerCount == 0)
                {
                    room.emptySince ??= DateTime.UtcNow;
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
            if (_roomIdToName.TryGetValue(roomId, out var name) && _room.TryGetValue(name, out var room) &&
                !room.registrationInProgress && !room.removalInProgress &&
                !Transport.TryGetRoomPlayerCount(roomId, out _))
            {
                room.registered = false;
                room.removed = true;
                room.removalInProgress = true;
                room.emptySince ??= DateTime.UtcNow;
                Transport.RemoveEmptyRoomState(roomId);
                FireAndForget(UnregisterRoom(room), $"unregisterRoom for room '{name}'");
            }
        }
    }

    static async Task UnregisterRoom(Room room)
    {
        try
        {
            await HTTPRestAPI.unegisterRoom(room.name!, room.instanceId);
            if (room.previousInstanceId != null)
                await HTTPRestAPI.unegisterRoom(room.name!, room.previousInstanceId);
            lock (_roomLock)
            {
                if (_room.TryGetValue(room.name!, out var current) && ReferenceEquals(current, room))
                {
                    _room.Remove(room.name!);
                    _roomIdToName.Remove(room.roomId);
                }
            }
        }
        finally
        {
            lock (_roomLock)
                room.removalInProgress = false;
        }
    }

    internal static int CleanupEmptyRooms(DateTime now, TimeSpan timeout)
    {
        lock (_roomLock)
        {
            var expired = _room.Values.Where(room => !room.registrationInProgress && !room.removalInProgress &&
                (room.removed || room.emptySince.HasValue && now - room.emptySince.Value >= timeout) &&
                !Transport.TryGetRoomPlayerCount(room.roomId, out _)).ToArray();

            foreach (var room in expired)
            {
                Console.WriteLine($"Removing empty room {room.name} (ID: {room.roomId})");
                RemoveRoom(room.roomId);
            }
            return expired.Length;
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

                CleanupEmptyRooms(DateTime.UtcNow, timeout);
            }
            catch (Exception e)
            {
                await Console.Error.WriteLineAsync($"Error in empty room cleanup task: {e.Message}\n{e.StackTrace}");
            }
        }
    }
}

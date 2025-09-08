namespace PurrLay;

public class Room
{
    public string? name;
    public string? hostSecret;
    public string? clientSecret;
    public DateTime createdAt;
    public ulong roomId;
}

public static class Lobby
{
    static readonly Dictionary<string, Room> _room = new();
    static readonly Dictionary<ulong, string> _roomIdToName = new();

    static ulong _roomIdCounter;

    public static async Task<string> CreateRoom(string region, string name)
    {
        var hostSecret = Guid.NewGuid().ToString().Replace("-", "");

        if (_room.TryGetValue(name, out var existing))
        {
            if (Transport.TryGetRoomPlayerCount(existing.roomId, out var currentCount) && currentCount > 0)
                throw new Exception("Room already exists");

            existing.hostSecret = hostSecret;
            existing.clientSecret = Guid.NewGuid().ToString().Replace("-", "");
            existing.createdAt = DateTime.UtcNow;
            return hostSecret;
        }

        await HTTPRestAPI.RegisterRoom(region, name);

        Console.WriteLine($"Registered room {name}");
        _roomIdToName.Add(_roomIdCounter, name);
        _room.Add(name, new Room
        {
            name = name,
            hostSecret = hostSecret,
            clientSecret = Guid.NewGuid().ToString().Replace("-", ""),
            createdAt = DateTime.UtcNow,
            roomId = _roomIdCounter++
        });

        return hostSecret;
    }

    public static bool TryGetRoom(string name, out Room? room)
    {
        return _room.TryGetValue(name, out room);
    }

    public static void UpdateRoomPlayerCount(ulong roomId, int newPlayerCount)
    {
        if (_roomIdToName.TryGetValue(roomId, out var name))
            _ = HTTPRestAPI.updateConnectionCount(name, newPlayerCount);
    }

    public static void RemoveRoom(ulong roomId)
    {
        if (_roomIdToName.Remove(roomId, out var name))
        {
            _room.Remove(name);
            _ = HTTPRestAPI.UnregisterRoom(name);
        }
    }
}

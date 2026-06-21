using System.Globalization;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using WatsonWebserver.Core;
using HttpMethod = System.Net.Http.HttpMethod;

namespace PurrBalancer;

[Serializable]
public struct RoomInfo
{
    public string name;
    public string region;
    public int connectedPlayers;
}

public static class HTTPRestAPI
{
    private static readonly List<RelayServer> _relayServers = [];

    public static async void StartHealthCheckService()
    {
        try
        {
            const int SECONDS_BETWEEN_CHECKS = 30;
            using var client = new HttpClient();

            while (true)
            {
                await Task.Delay(SECONDS_BETWEEN_CHECKS * 1000);

                int relayCount;

                lock (_relayServers)
                {
                    relayCount = _relayServers.Count;
                    if (relayCount == 0)
                        continue;
                }

                for (var index = 0; index < relayCount; index++)
                {
                    string endpoint;
                    lock (_relayServers)
                    {
                        relayCount = _relayServers.Count;
                        if (index >= relayCount)
                            break;
                        endpoint = _relayServers[index].apiEndpoint;
                    }

                    bool success;

                    try
                    {
                        using var res = await client.GetAsync($"{endpoint}/ping");
                        success = res.IsSuccessStatusCode;
                    }
                    catch
                    {
                        success = false;
                    }

                    if (!success)
                    {
                        var removedServer = false;
                        lock (_relayServers)
                        {
                            for (var i = index; i < relayCount; i++)
                            {
                                if (_relayServers[i].apiEndpoint == endpoint)
                                {
                                    _relayServers.RemoveAt(i);
                                    removedServer = true;
                                    index--;
                                    break;
                                }
                            }
                        }

                        if (removedServer)
                            RemoveRoomsForServerEndpoint(endpoint);

                        await Console.Error.WriteLineAsync($"PurrBalancer: Server `{endpoint}` is down");
                    }
                }
            }
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"Error StartHealthCheckService: {e.Message}\n{e.StackTrace}");
        }
    }

    static bool TryGetServer(string region, out RelayServer server)
    {
        lock (_relayServers)
        {
            for (var i = 0; i < _relayServers.Count; i++)
            {
                var s = _relayServers[i];
                if (s.region == region)
                {
                    server = s;
                    return true;
                }
            }

            server = default;
            return false;
        }
    }

    static bool TryGetServerByEndpoint(string endpoint, out RelayServer server)
    {
        lock (_relayServers)
        {
            for (var i = 0; i < _relayServers.Count; i++)
            {
                var candidate = _relayServers[i];
                if (string.Equals(candidate.apiEndpoint, endpoint, StringComparison.Ordinal))
                {
                    server = candidate;
                    return true;
                }
            }

            server = default;
            return false;
        }
    }

    static readonly Dictionary<string, string> _roomToRegion = new();
    static readonly Dictionary<string, string> _roomToServerEndpoint = new();
    private static readonly object _roomToRegionLock = new();
    private static readonly object _roomsLock = new();
    private static readonly object _emptyRoomsLock = new();

    private static readonly List<RoomInfo> _rooms = new();
    private static readonly Dictionary<string, DateTime> _emptyRoomSince = new();

    public static async void StartEmptyRoomCleanupService()
    {
        try
        {
            const int DEFAULT_TIMEOUT_SECONDS = 300;
            const int SECONDS_BETWEEN_CHECKS = 30;
            var timeoutSeconds = Env.TryGetIntOrDefault("EMPTY_ROOM_TIMEOUT_SECONDS", DEFAULT_TIMEOUT_SECONDS);
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);

            while (true)
            {
                await Task.Delay(SECONDS_BETWEEN_CHECKS * 1000);

                try
                {
                    var now = DateTime.UtcNow;
                    List<string> roomsToRemove = [];

                    lock (_emptyRoomsLock)
                    {
                        foreach (var (roomName, emptySince) in _emptyRoomSince)
                        {
                            if (now - emptySince >= timeout)
                                roomsToRemove.Add(roomName);
                        }
                    }

                    if (roomsToRemove.Count == 0)
                        continue;

                    RemoveRoomsByName(roomsToRemove);
                }
                catch (Exception e)
                {
                    await Console.Error.WriteLineAsync($"Error StartEmptyRoomCleanupService tick: {e.Message}\n{e.StackTrace}");
                }
            }
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"Error StartEmptyRoomCleanupService: {e.Message}\n{e.StackTrace}");
        }
    }

    static bool TryGetRoomServer(string roomName, out RelayServer server)
    {
        string? endpoint;
        lock (_roomToRegionLock)
        {
            if (!_roomToServerEndpoint.TryGetValue(roomName, out endpoint))
            {
                server = default;
                return false;
            }
        }

        return TryGetServerByEndpoint(endpoint, out server);
    }

    static void RemoveRoomsForServerEndpoint(string endpoint)
    {
        List<string> roomsToRemove = [];

        lock (_roomToRegionLock)
        {
            foreach (var room in _roomToServerEndpoint)
            {
                if (string.Equals(room.Value, endpoint, StringComparison.Ordinal))
                    roomsToRemove.Add(room.Key);
            }

            for (var i = 0; i < roomsToRemove.Count; i++)
            {
                _roomToServerEndpoint.Remove(roomsToRemove[i]);
                _roomToRegion.Remove(roomsToRemove[i]);
            }
        }

        if (roomsToRemove.Count == 0)
            return;

        RemoveEmptyRoomTracking(roomsToRemove);

        lock (_roomsLock)
        {
            for (var i = _rooms.Count - 1; i >= 0; i--)
            {
                if (roomsToRemove.Contains(_rooms[i].name))
                    _rooms.RemoveAt(i);
            }
        }
    }

    static void TrackRoomPlayerCount(string name, int count)
    {
        lock (_emptyRoomsLock)
        {
            if (count <= 0)
            {
                _emptyRoomSince.TryAdd(name, DateTime.UtcNow);
                return;
            }

            _emptyRoomSince.Remove(name);
        }
    }

    static void RemoveEmptyRoomTracking(IReadOnlyCollection<string> roomNames)
    {
        lock (_emptyRoomsLock)
        {
            foreach (var roomName in roomNames)
                _emptyRoomSince.Remove(roomName);
        }
    }

    static void RemoveRoomsByName(IReadOnlyCollection<string> roomNames)
    {
        if (roomNames.Count == 0)
            return;

        List<string> removedRooms = [];

        lock (_roomToRegionLock)
        {
            foreach (var roomName in roomNames)
            {
                if (!_roomToRegion.Remove(roomName, out _))
                    continue;

                _roomToServerEndpoint.Remove(roomName);
                removedRooms.Add(roomName);
            }
        }

        if (removedRooms.Count == 0)
            return;

        RemoveEmptyRoomTracking(removedRooms);

        lock (_roomsLock)
        {
            for (var i = _rooms.Count - 1; i >= 0; i--)
            {
                if (removedRooms.Contains(_rooms[i].name))
                    _rooms.RemoveAt(i);
            }
        }

        Console.WriteLine($"Removed {removedRooms.Count} empty room(s): {string.Join(", ", removedRooms)}");
    }

    public static async Task<ApiResponse> OnRequest(HttpRequestBase req)
    {
        if (req.Url == null)
            throw new Exception("Invalid URL");

        switch (req.Url.RawWithoutQuery)
        {
            case "/":
                return new ApiResponse(DateTime.Now.ToString(CultureInfo.InvariantCulture));
            case "/ping":
                return new ApiResponse(HttpStatusCode.OK);
            case "/servers":
                lock (_relayServers)
                    return new ApiResponse(new JObject { ["servers"] = JArray.FromObject(_relayServers) });
            case "/registerServer":
                return RegisterServer(req);
            case "/unregisterServer":
                return UnregisterServer(req);
            case "/registerRoom":
                return RegisterRoom(req);
            case "/unregisterRoom":
                return UnregisterRoom(req);
            case "/updateConnectionCount":
                return UpdateConnectionCount(req);
            case "/join":
                return await HandleJoin(req);
            case "/allocate_ws":
                return await AllocateRoom(req);
            case "/migration/claim":
                return await ClaimMigration(req);
            case "/migration/current":
                return await GetMigrationCurrent(req);
            case "/list":
                return await SearchRooms(req);
            case "/getTotalConnections":
                return GetTotalConnections(req);
            default:
                return new ApiResponse(HttpStatusCode.NotFound);
        }
    }

    private static async Task<ApiResponse> ClaimMigration(HttpRequestBase req)
    {
        return await ForwardMigrationRequest(req, "/migration/claim", true);
    }

    private static async Task<ApiResponse> GetMigrationCurrent(HttpRequestBase req)
    {
        return await ForwardMigrationRequest(req, "/migration/current", false);
    }

    private static async Task<ApiResponse> ForwardMigrationRequest(
        HttpRequestBase req,
        string relayPath,
        bool includeClaimHeaders)
    {
        if (req.Method != WatsonWebserver.Core.HttpMethod.GET)
            return new ApiResponse(HttpStatusCode.NoContent);

        var name = req.RetrieveHeaderValue("name");

        if (string.IsNullOrEmpty(name))
            throw new Exception("PurrBalancer_migration: Invalid headers");

        if (!TryGetRoomServer(name, out var server))
            throw new Exception("PurrBalancer: Room not found");

        using HttpClient client = new();

        client.DefaultRequestHeaders.Add("name", name);
        client.DefaultRequestHeaders.Add("region", server.region);
        client.DefaultRequestHeaders.Add("internal_key_secret", Program.SECRET_INTERNAL);

        if (includeClaimHeaders)
        {
            AddOptionalForwardedHeader(client, req, "migration_secret");
            AddOptionalForwardedHeader(client, req, "client_secret");
            AddOptionalForwardedHeader(client, req, "host_secret");
            AddOptionalForwardedHeader(client, req, "secret");
            AddOptionalForwardedHeader(client, req, "promoted_player_id");
            AddOptionalForwardedHeader(client, req, "promoted_player");
            AddOptionalForwardedHeader(client, req, "expected_generation");
            AddOptionalForwardedHeader(client, req, "previous_generation");
        }

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{server.apiEndpoint}{relayPath}"));

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsByteArrayAsync();
            var contentStr = Encoding.UTF8.GetString(content);
            throw new Exception(contentStr);
        }

        try
        {
            var respStr = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(respStr);
            obj["host"] = server.host;
            return new ApiResponse(obj);
        }
        catch (Exception e)
        {
            throw new Exception("Invalid response " + e.Message + "\n" + e.StackTrace);
        }
    }

    private static void AddOptionalForwardedHeader(HttpClient client, HttpRequestBase req, string name)
    {
        var value = req.RetrieveHeaderValue(name);

        if (!string.IsNullOrWhiteSpace(value))
            client.DefaultRequestHeaders.Add(name, value);
    }

    private static Task<ApiResponse> SearchRooms(HttpRequestBase req)
    {
        const int PAGE_SIZE = 50;

        if (req.Method != WatsonWebserver.Core.HttpMethod.GET)
            return Task.FromResult(new ApiResponse(HttpStatusCode.NoContent));

        var pageNumberStr = req.RetrieveQueryValue("page") ?? "0";

        if (!int.TryParse(pageNumberStr, out var pg))
            throw new Exception("PurrBalancer_SearchRooms: Invalid page");

        int startIdx = pg * PAGE_SIZE;
        var response = new JObject();
        var servers = new JArray();

        lock (_roomsLock)
        {
            for (int i = startIdx; i < _rooms.Count; ++i)
            {
                servers.Add(JObject.FromObject(_rooms[i]));
            }

            response.Add("results", servers);
            response.Add("total", _rooms.Count);
        }
        return Task.FromResult(new ApiResponse(response));
    }

    private static async Task<ApiResponse> AllocateRoom(HttpRequestBase req)
    {
        if (req.Method != WatsonWebserver.Core.HttpMethod.GET)
            return new ApiResponse(HttpStatusCode.NoContent);

        var region = req.RetrieveHeaderValue("region");
        var name = req.RetrieveHeaderValue("name");

        if (string.IsNullOrEmpty(region))
            throw new Exception("PurrBalancer_allocate: Invalid headers");

        if (!TryGetServer(region, out var server))
            throw new Exception($"PurrBalancer: Invalid region `{region}`");

        if (string.IsNullOrEmpty(name))
            throw new Exception("PurrBalancer: Invalid name");

        using HttpClient client = new();

        client.DefaultRequestHeaders.Add("name", name);
        client.DefaultRequestHeaders.Add("region", region);
        client.DefaultRequestHeaders.Add("internal_key_secret", Program.SECRET_INTERNAL);

        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{server.apiEndpoint}/allocate_ws"));

        if (!resp.IsSuccessStatusCode)
        {
            var content = resp.Content.ReadAsByteArrayAsync();
            var contentStr = Encoding.UTF8.GetString(content.Result);
            throw new Exception(contentStr);
        }

        try
        {
            var respStr = await resp.Content.ReadAsByteArrayAsync();
            return new ApiResponse(respStr, HttpStatusCode.OK, ContentType.JSON);
        }
        catch (Exception e)
        {
            throw new Exception("Invalid response " + e.Message + "\n" + e.StackTrace);
        }
    }

    private static async Task<ApiResponse> HandleJoin(HttpRequestBase req)
    {
        if (req.Method != WatsonWebserver.Core.HttpMethod.GET)
            return new ApiResponse(HttpStatusCode.NoContent);

        var name = req.RetrieveHeaderValue("name");

        if (string.IsNullOrEmpty(name))
            throw new Exception("PurrBalancer_join: Invalid headers");

        if (!TryGetRoomServer(name, out var server))
            throw new Exception("PurrBalancer: Room not found");

        using HttpClient client = new();

        client.DefaultRequestHeaders.Add("name", name);
        client.DefaultRequestHeaders.Add("region", server.region);
        client.DefaultRequestHeaders.Add("internal_key_secret", Program.SECRET_INTERNAL);

        var r = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{server.apiEndpoint}/getJoinDetails"));

        if (!r.IsSuccessStatusCode)
        {
            var content = r.Content.ReadAsByteArrayAsync();
            var contentStr = Encoding.UTF8.GetString(content.Result);
            throw new Exception(contentStr);
        }

        try
        {
            var respStr = await r.Content.ReadAsStringAsync();
            var obj = JObject.Parse(respStr);
            obj["host"] = server.host;
            return new ApiResponse(obj);
        }
        catch (Exception e)
        {
            throw new Exception("Invalid response " + e.Message + "\n" + e.StackTrace);
        }
    }

    private static ApiResponse UnregisterRoom(HttpRequestBase req)
    {
        var name = req.RetrieveHeaderValue("name");
        var internalSecret = req.RetrieveHeaderValue("internal_key_secret");

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(internalSecret))
            throw new Exception("PurrBalancer_unregisterRoom: Invalid headers");

        if (!string.Equals(internalSecret, Program.SECRET_INTERNAL))
            throw new Exception("PurrBalancer: Invalid internal secret");

        lock (_roomToRegionLock)
        {
            if (!_roomToRegion.Remove(name, out _))
                throw new Exception("PurrBalancer: Room not found");

            _roomToServerEndpoint.Remove(name);
        }

        RemoveEmptyRoomTracking(new[] { name });

        lock (_roomsLock)
        {
            for (var i = 0; i < _rooms.Count; i++)
            {
                if (_rooms[i].name == name)
                {
                    _rooms.RemoveAt(i);
                    break;
                }
            }
        }

        return new ApiResponse(new JObject
        {
            ["status"] = "ok"
        });
    }

    private static ApiResponse UpdateConnectionCount(HttpRequestBase req)
    {
        var name = req.RetrieveHeaderValue("name");
        var internalSecret = req.RetrieveHeaderValue("internal_key_secret");
        var count = req.RetrieveHeaderValue("count");

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(internalSecret))
            throw new Exception("PurrBalancer_unregisterRoom: Invalid headers");

        if (!string.Equals(internalSecret, Program.SECRET_INTERNAL))
            throw new Exception("PurrBalancer: Invalid internal secret");

        if (!int.TryParse(count, out var countNumber))
            throw new Exception("PurrBalancer: Invalid count");

        lock (_roomToRegionLock)
        {
            if (!_roomToRegion.ContainsKey(name))
                throw new Exception("PurrBalancer: Room not found");
        }

        TrackRoomPlayerCount(name, countNumber);

        lock (_roomsLock)
        {
            for (var i = 0; i < _rooms.Count; i++)
            {
                var room = _rooms[i];
                if (room.name == name)
                {
                    room.connectedPlayers = countNumber;
                    _rooms[i] = room;
                    break;
                }
            }
        }

        return new ApiResponse(new JObject
        {
            ["status"] = "ok"
        });
    }

    private static ApiResponse RegisterRoom(HttpRequestBase req)
    {
        var region = req.RetrieveHeaderValue("region");
        var name = req.RetrieveHeaderValue("name");
        var relayEndpoint = req.RetrieveHeaderValue("relay_endpoint");
        var internalSecret = req.RetrieveHeaderValue("internal_key_secret");

        if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(internalSecret))
            throw new Exception("PurrBalancer_registerRoom: Invalid headers");

        if (!string.Equals(internalSecret, Program.SECRET_INTERNAL))
            throw new Exception("PurrBalancer: Invalid internal secret");

        RelayServer server;
        if (!string.IsNullOrEmpty(relayEndpoint))
        {
            if (!TryGetServerByEndpoint(relayEndpoint, out server))
                throw new Exception("PurrBalancer: Invalid relay endpoint when registering room");

            if (!string.Equals(server.region, region, StringComparison.Ordinal))
                throw new Exception("PurrBalancer: Relay endpoint region mismatch when registering room");
        }
        else if (!TryGetServer(region, out server))
        {
            throw new Exception("PurrBalancer: Invalid region when registering room");
        }

        lock (_roomToRegionLock)
        {
            if (!_roomToRegion.TryAdd(name, region))
                throw new Exception("PurrBalancer: Room already registered");

            _roomToServerEndpoint.Add(name, server.apiEndpoint);
        }

        TrackRoomPlayerCount(name, 0);

        lock (_roomsLock)
        {
            _rooms.Add(new RoomInfo
            {
                name = name,
                region = region,
                connectedPlayers = 0
            });
        }

        return new ApiResponse(new JObject
        {
            ["status"] = "ok"
        });
    }

    private static ApiResponse RegisterServer(HttpRequestBase req)
    {
        if (req.Method != WatsonWebserver.Core.HttpMethod.POST)
            throw new Exception("PurrBalancer: Invalid method");

        var internalSecret = req.RetrieveHeaderValue("internal_key_secret");
        if (!string.Equals(internalSecret, Program.SECRET_INTERNAL))
            throw new Exception("PurrBalancer: Invalid internal secret");

        var body = req.DataAsString;
        var server = JObject.Parse(body).ToObject<RelayServer>();

        lock (_relayServers)
        {
            for (var i = 0; i < _relayServers.Count; i++)
            {
                if (string.Equals(_relayServers[i].apiEndpoint, server.apiEndpoint, StringComparison.Ordinal))
                {
                    _relayServers[i] = server;
                    return new ApiResponse(new JObject
                    {
                        ["status"] = "ok"
                    });
                }
            }

            _relayServers.Add(server);
        }

        return new ApiResponse(new JObject
        {
            ["status"] = "ok"
        });
    }

    private static ApiResponse UnregisterServer(HttpRequestBase req)
    {
        if (req.Method != WatsonWebserver.Core.HttpMethod.POST)
            throw new Exception("PurrBalancer: Invalid method");

        var internalSecret = req.RetrieveHeaderValue("internal_key_secret");

        if (!string.Equals(internalSecret, Program.SECRET_INTERNAL))
            throw new Exception("PurrBalancer: Invalid internal secret");

        var body = req.DataAsString;
        var server = JObject.Parse(body).ToObject<RelayServer>();

        var removedServer = false;
        lock (_relayServers)
        {
            for (var i = 0; i < _relayServers.Count; i++)
            {
                if (string.Equals(_relayServers[i].apiEndpoint, server.apiEndpoint, StringComparison.Ordinal))
                {
                    _relayServers.RemoveAt(i);
                    removedServer = true;
                    break;
                }
            }
        }

        if (removedServer)
            RemoveRoomsForServerEndpoint(server.apiEndpoint);

        return new ApiResponse(new JObject
        {
            ["status"] = "ok"
        });
    }

    private static ApiResponse GetTotalConnections(HttpRequestBase req)
    {
        int totalConnections = 0;
        
        lock (_roomsLock)
        {
            for (var i = 0; i < _rooms.Count; i++)
            {
                totalConnections += _rooms[i].connectedPlayers;
            }
        }
        
        return new ApiResponse(JObject.FromObject(new
        {
            totalConnections = totalConnections
        }));
    }
}

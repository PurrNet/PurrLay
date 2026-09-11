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

public static partial class HTTPRestAPI
{
    private static readonly List<RelayServer> _relayServers = [];
    private static readonly Dictionary<string, string> _lastRelayInstanceIds = new();
    private static readonly Dictionary<string, HashSet<string>> _retiredRelayInstanceIds = new();

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
                    string? relayInstanceId;
                    lock (_relayServers)
                    {
                        relayCount = _relayServers.Count;
                        if (index >= relayCount)
                            break;
                        endpoint = _relayServers[index].apiEndpoint;
                        relayInstanceId = _relayServers[index].instanceId;
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
                        lock (StateGate)
                        {
                            if (!IsAuthority)
                                continue;
                            lock (_relayServers)
                            {
                                for (var i = 0; i < _relayServers.Count; i++)
                                {
                                    if (_relayServers[i].apiEndpoint == endpoint &&
                                        string.Equals(_relayServers[i].instanceId, relayInstanceId, StringComparison.Ordinal))
                                    {
                                        _relayServers.RemoveAt(i);
                                        RemoveRoomsForServerEndpoint(endpoint);
                                        PersistCheckpoint();
                                        index--;
                                        break;
                                    }
                                }
                            }
                        }

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
                if (s.region == region && !s.draining)
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
    private static readonly object _roomsLock = new();

    private static readonly List<RoomInfo> _rooms = new();
    private static readonly Dictionary<string, DateTime> _emptyRoomSince = new();
    private static readonly Dictionary<string, string> _roomInstanceIds = new();
    private static readonly Dictionary<string, long> _roomCountSequences = new();

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
                    CleanupEmptyRooms(DateTime.UtcNow, timeout);
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
        lock (_roomsLock)
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
        lock (_roomsLock)
        {
            List<string> roomsToRemove = [];
            foreach (var room in _roomToServerEndpoint)
            {
                if (string.Equals(room.Value, endpoint, StringComparison.Ordinal))
                    roomsToRemove.Add(room.Key);
            }

            foreach (var roomName in roomsToRemove)
                RemoveRoomUnderLock(roomName);
        }
    }

    static void TrackRoomPlayerCountUnderLock(string name, int count)
    {
        if (count == 0 && !_roomInstanceIds.ContainsKey(name))
        {
            _emptyRoomSince.TryAdd(name, DateTime.UtcNow);
            return;
        }

        _emptyRoomSince.Remove(name);
    }

    static void RemoveRoomUnderLock(string name)
    {
        _roomToRegion.Remove(name);
        _roomToServerEndpoint.Remove(name);
        _roomInstanceIds.Remove(name);
        _roomCountSequences.Remove(name);
        _emptyRoomSince.Remove(name);
        for (var i = _rooms.Count - 1; i >= 0; i--)
        {
            if (_rooms[i].name == name)
                _rooms.RemoveAt(i);
        }
    }

    internal static int CleanupEmptyRooms(DateTime now, TimeSpan timeout)
    {
        List<string> removedRooms = [];
        lock (StateGate)
        {
            if (!IsAuthority)
                return 0;
            lock (_roomsLock)
            {
                foreach (var room in _rooms)
                {
                    if (!_roomInstanceIds.ContainsKey(room.name) &&
                        room.connectedPlayers == 0 &&
                        _emptyRoomSince.TryGetValue(room.name, out var emptySince) &&
                        now - emptySince >= timeout)
                    {
                        removedRooms.Add(room.name);
                    }
                }

                foreach (var roomName in removedRooms)
                    RemoveRoomUnderLock(roomName);
            }
            if (removedRooms.Count > 0)
                PersistCheckpoint();
        }

        if (removedRooms.Count > 0)
            Console.WriteLine($"Removed {removedRooms.Count} empty room(s): {string.Join(", ", removedRooms)}");
        return removedRooms.Count;
    }

    private static async Task<ApiResponse> OnLocalRequest(HttpRequestBase req)
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
                    return PublicServers(_relayServers.Where(server => !server.draining)
                        .GroupBy(server => server.region).Select(group => group.First()));
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
        var relayEndpoint = req.RetrieveHeaderValue("relay_endpoint");
        var instanceId = req.RetrieveHeaderValue("room_instance_id");

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(internalSecret))
            throw new Exception("PurrBalancer_unregisterRoom: Invalid headers");

        if (!string.Equals(internalSecret, Program.SECRET_INTERNAL))
            throw new Exception("PurrBalancer: Invalid internal secret");

        lock (_roomsLock)
        {
            if (MatchesRoomOwnerUnderLock(name, relayEndpoint, instanceId))
                RemoveRoomUnderLock(name);
        }

        return RoomMutationAccepted();
    }

    static ApiResponse RoomMutationAccepted() => new(new JObject { ["status"] = "ok" });

    static bool MatchesRoomOwnerUnderLock(string name, string? relayEndpoint, string? instanceId)
    {
        if (!_roomToServerEndpoint.TryGetValue(name, out var currentEndpoint))
            return false;

        if (_roomInstanceIds.TryGetValue(name, out var currentInstanceId))
        {
            return string.Equals(currentInstanceId, instanceId, StringComparison.Ordinal) &&
                   string.Equals(currentEndpoint, relayEndpoint, StringComparison.Ordinal);
        }

        return string.IsNullOrEmpty(instanceId) &&
               (string.IsNullOrEmpty(relayEndpoint) ||
                string.Equals(currentEndpoint, relayEndpoint, StringComparison.Ordinal));
    }

    private static ApiResponse UpdateConnectionCount(HttpRequestBase req)
    {
        var name = req.RetrieveHeaderValue("name");
        var internalSecret = req.RetrieveHeaderValue("internal_key_secret");
        var count = req.RetrieveHeaderValue("count");
        var relayEndpoint = req.RetrieveHeaderValue("relay_endpoint");
        var instanceId = req.RetrieveHeaderValue("room_instance_id");

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(internalSecret))
            throw new Exception("PurrBalancer_updateConnectionCount: Invalid headers");

        if (!string.Equals(internalSecret, Program.SECRET_INTERNAL))
            throw new Exception("PurrBalancer: Invalid internal secret");

        if (!int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var countNumber) || countNumber < 0)
            throw new Exception("PurrBalancer: Invalid count");

        lock (_roomsLock)
        {
            if (!MatchesRoomOwnerUnderLock(name, relayEndpoint, instanceId))
                return RoomMutationAccepted();

            if (_roomInstanceIds.ContainsKey(name))
            {
                var sequenceHeader = req.RetrieveHeaderValue("count_sequence");
                if (!long.TryParse(sequenceHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ||
                    sequence < 0)
                {
                    throw new Exception("PurrBalancer: Invalid count sequence");
                }

                if (sequence <= _roomCountSequences[name])
                    return RoomMutationAccepted();

                _roomCountSequences[name] = sequence;
            }

            TrackRoomPlayerCountUnderLock(name, countNumber);

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

        return RoomMutationAccepted();
    }

    private static ApiResponse RegisterRoom(HttpRequestBase req)
    {
        var region = req.RetrieveHeaderValue("region");
        var name = req.RetrieveHeaderValue("name");
        var relayEndpoint = req.RetrieveHeaderValue("relay_endpoint");
        var instanceId = req.RetrieveHeaderValue("room_instance_id");
        var previousInstanceId = req.RetrieveHeaderValue("previous_room_instance_id");
        var relayInstanceId = req.RetrieveHeaderValue("relay_instance_id");
        var internalSecret = req.RetrieveHeaderValue("internal_key_secret");

        if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(internalSecret))
            throw new Exception("PurrBalancer_registerRoom: Invalid headers");

        if (!string.Equals(internalSecret, Program.SECRET_INTERNAL))
            throw new Exception("PurrBalancer: Invalid internal secret");

        lock (_relayServers)
        {
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

            if (!string.IsNullOrEmpty(server.instanceId) &&
                !string.Equals(server.instanceId, relayInstanceId, StringComparison.Ordinal))
            {
                return ApiResponse.FromError("PurrBalancer: Relay instance changed", HttpStatusCode.Conflict);
            }

            lock (_roomsLock)
            {
                if (_roomToServerEndpoint.TryGetValue(name, out var existingEndpoint))
                {
                    var sameEndpoint = string.Equals(existingEndpoint, server.apiEndpoint, StringComparison.Ordinal);
                    var existingIsVersioned = _roomInstanceIds.TryGetValue(name, out var existingInstanceId);
                    var newIsVersioned = !string.IsNullOrEmpty(instanceId);

                    if (sameEndpoint && existingIsVersioned &&
                        string.Equals(existingInstanceId, instanceId, StringComparison.Ordinal))
                    {
                        return RoomMutationAccepted();
                    }

                    var replacesExpectedInstance = existingIsVersioned &&
                        string.Equals(existingInstanceId, previousInstanceId, StringComparison.Ordinal);
                    var upgradesEmptyLegacyRoom = !existingIsVersioned &&
                        _rooms.Exists(room => room.name == name && room.connectedPlayers == 0);

                    if (!sameEndpoint || !newIsVersioned ||
                        (!replacesExpectedInstance && !upgradesEmptyLegacyRoom))
                    {
                        return ApiResponse.FromError("PurrBalancer: Room already registered", HttpStatusCode.Conflict);
                    }

                    RemoveRoomUnderLock(name);
                }

                _roomToRegion.Add(name, region);
                _roomToServerEndpoint.Add(name, server.apiEndpoint);
                if (!string.IsNullOrEmpty(instanceId))
                {
                    _roomInstanceIds.Add(name, instanceId);
                    _roomCountSequences.Add(name, -1);
                }

                TrackRoomPlayerCountUnderLock(name, 0);
                _rooms.Add(new RoomInfo
                {
                    name = name,
                    region = region,
                    connectedPlayers = 0
                });
            }
        }

        return RoomMutationAccepted();
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
        if (string.IsNullOrWhiteSpace(server.apiEndpoint) || string.IsNullOrWhiteSpace(server.region))
            return ApiResponse.FromError("Invalid relay registration", HttpStatusCode.BadRequest);

        lock (_relayServers)
        {
            if (!string.IsNullOrEmpty(server.instanceId))
            {
                if (_retiredRelayInstanceIds.TryGetValue(server.apiEndpoint, out var retiredInstances) &&
                    retiredInstances.Contains(server.instanceId))
                {
                    server.draining = true;
                    return ServerRegistrationAccepted(server);
                }

                if (_lastRelayInstanceIds.TryGetValue(server.apiEndpoint, out var previousInstanceId) &&
                    !string.Equals(previousInstanceId, server.instanceId, StringComparison.Ordinal))
                {
                    if (retiredInstances == null)
                    {
                        retiredInstances = new HashSet<string>(StringComparer.Ordinal);
                        _retiredRelayInstanceIds.Add(server.apiEndpoint, retiredInstances);
                    }
                    retiredInstances.Add(previousInstanceId);
                }

                _lastRelayInstanceIds[server.apiEndpoint] = server.instanceId;
            }

            for (var i = 0; i < _relayServers.Count; i++)
            {
                if (string.Equals(_relayServers[i].apiEndpoint, server.apiEndpoint, StringComparison.Ordinal))
                {
                    server.draining |= _relayServers[i].draining || _drainingRelayEndpoints.Contains(server.apiEndpoint);
                    if (IsDesiredActiveRelay(server)) server.draining = false;
                    if (!string.Equals(_relayServers[i].instanceId, server.instanceId, StringComparison.Ordinal))
                        RemoveRoomsForServerEndpoint(server.apiEndpoint);
                    _relayServers[i] = server;
                    return ServerRegistrationAccepted(server);
                }
            }

            server.draining |= _drainingRelayEndpoints.Contains(server.apiEndpoint);
            if (IsDesiredActiveRelay(server)) server.draining = false;
            _relayServers.Add(server);
        }

        return ServerRegistrationAccepted(server);
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

        lock (_relayServers)
        {
            for (var i = 0; i < _relayServers.Count; i++)
            {
                if (string.Equals(_relayServers[i].apiEndpoint, server.apiEndpoint, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(_relayServers[i].instanceId) &&
                        !string.Equals(_relayServers[i].instanceId, server.instanceId, StringComparison.Ordinal))
                    {
                        return RoomMutationAccepted();
                    }

                    _relayServers.RemoveAt(i);
                    RemoveRoomsForServerEndpoint(server.apiEndpoint);
                    break;
                }
            }
        }

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

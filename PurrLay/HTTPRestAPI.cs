using System.Globalization;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PurrBalancer;
using WatsonWebserver.Core;
using HttpMethod = System.Net.Http.HttpMethod;

namespace PurrLay;

public static class HTTPRestAPI
{
    public static WebSockets? webServer;
    public static IUdpServer? udpServerV1;
    public static IUdpServer? udpServerV2;
    public static IUdpServer? webRtcServer;
    internal static WebRtcGatewayRuntime? webRtcRuntime;

    /// <summary>
    /// Delivery-framed connection backends: 1 = LiteNetLib V1, 2 = V2, 3 = WebRTC.
    /// </summary>
    static readonly Dictionary<int, int> _connToUdpVersion = new();
    static readonly object _versionLock = new();

    internal static UdpServerCallbacks CreateCallbacks(int version) => new()
    {
        ReserveConnId = isUdp =>
        {
            var connId = Transport.ReserveConnId(isUdp);
            lock (_versionLock)
            {
                _connToUdpVersion[connId] = version;
            }
            return connId;
        },
        OnClientLeft = Transport.OnClientLeft,
        OnDataReceived = Transport.OnServerReceivedData
    };

    /// <summary>
    /// Returns the correct UDP server for the given connection ID,
    /// based on which server that connection came through.
    /// </summary>
    public static IUdpServer? GetUdpServerForConnection(int connId)
    {
        int version;
        lock (_versionLock)
        {
            if (!_connToUdpVersion.TryGetValue(connId, out version))
                return udpServerV1; // fallback to V1
        }
        return version switch { 2 => udpServerV2, 3 => webRtcServer, _ => udpServerV1 };
    }

    /// <summary>
    /// Returns true if the connection came through the UDP V2 server (LiteNetLib 2.x).
    /// NAT hole-punching is only offered between two V2 UDP peers.
    /// </summary>
    public static bool IsUdpV2(int connId)
    {
        lock (_versionLock)
            return _connToUdpVersion.TryGetValue(connId, out var version) && version == 2;
    }

    /// <summary>
    /// Removes the UDP version tracking entry for a disconnected connection.
    /// </summary>
    public static void RemoveUdpVersionTracking(int connId)
    {
        lock (_versionLock)
        {
            _connToUdpVersion.Remove(connId);
        }
    }

    public static async Task RegisterRoom(string region, string roomName, string? instanceId = null, string? previousInstanceId = null)
    {
        if (!Env.TryGetValue("BALANCER_URL", out var balancerUrl))
            throw new Exception("Missing `BALANCER_URL` env variable");

        using HttpClient client = new();

        client.DefaultRequestHeaders.Add("name", roomName);
        client.DefaultRequestHeaders.Add("region", region);
        client.DefaultRequestHeaders.Add("relay_endpoint", Program.GetRelayEndpoint());
        client.DefaultRequestHeaders.Add("relay_instance_id", Program.ProcessInstanceId);
        if (instanceId != null)
            client.DefaultRequestHeaders.Add("room_instance_id", instanceId);
        if (previousInstanceId != null)
            client.DefaultRequestHeaders.Add("previous_room_instance_id", previousInstanceId);
        client.DefaultRequestHeaders.Add("internal_key_secret", Program.SECRET_INTERNAL);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{balancerUrl}/registerRoom"));

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(await response.Content.ReadAsStringAsync());
        }
    }

    public static async Task unegisterRoom(string roomName, string? instanceId = null)
    {
        if (!Env.TryGetValue("BALANCER_URL", out var balancerUrl))
            throw new Exception("Missing `BALANCER_URL` env variable");

        using HttpClient client = new();

        client.DefaultRequestHeaders.Add("name", roomName);
        client.DefaultRequestHeaders.Add("relay_endpoint", Program.GetRelayEndpoint());
        if (instanceId != null)
            client.DefaultRequestHeaders.Add("room_instance_id", instanceId);
        client.DefaultRequestHeaders.Add("internal_key_secret", Program.SECRET_INTERNAL);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{balancerUrl}/unregisterRoom"));

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(await response.Content.ReadAsStringAsync());
        }
    }

    public static async Task updateConnectionCount(string roomName, int newCount, string? instanceId = null, long countSequence = 0)
    {
        if (!Env.TryGetValue("BALANCER_URL", out var balancerUrl))
            throw new Exception("Missing `BALANCER_URL` env variable");

        using HttpClient client = new();

        client.DefaultRequestHeaders.Add("name", roomName);
        client.DefaultRequestHeaders.Add("relay_endpoint", Program.GetRelayEndpoint());
        if (instanceId != null)
        {
            client.DefaultRequestHeaders.Add("room_instance_id", instanceId);
            client.DefaultRequestHeaders.Add("count_sequence", countSequence.ToString(CultureInfo.InvariantCulture));
        }
        client.DefaultRequestHeaders.Add("internal_key_secret", Program.SECRET_INTERNAL);
        client.DefaultRequestHeaders.Add("count", newCount.ToString());

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{balancerUrl}/updateConnectionCount"));

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(await response.Content.ReadAsStringAsync());
        }
    }

    [Serializable]
    internal struct ClientJoinInfo
    {
        public bool ssl;
        public string? secret;
        public int port;
        public int udpPort;
        public int udpPortV2;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? webRtcUrl;
    }

    [Serializable]
    internal struct MigrationJoinInfo
    {
        public bool ssl;
        public string? secret;
        public int port;
        public int udpPort;
        public int udpPortV2;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? webRtcUrl;
        public string? roomName;
        public int generation;
        public string? fencingToken;
        public string? promotedPlayerId;
        public string? claimedAt;
    }

    public static async Task<ApiResponse> OnRequest(HttpRequestBase req)
    {
        if (req.Url == null)
            throw new Exception("Invalid URL");

        string path = req.Url.RawWithoutQuery;

        if (req.Method == WatsonWebserver.Core.HttpMethod.OPTIONS)
            return new ApiResponse(HttpStatusCode.NoContent);

        switch (path)
        {
            case "/": return new ApiResponse(DateTime.Now.ToString(CultureInfo.InvariantCulture));
            case "/ping": return new ApiResponse(HttpStatusCode.OK);
            case "/webrtc/offer":
                if (req.Method != WatsonWebserver.Core.HttpMethod.POST)
                    return new ApiResponse(HttpStatusCode.MethodNotAllowed);
                return webRtcRuntime is { Available: true } runtime
                    ? await runtime.OfferAsync(req)
                    : ApiResponse.FromError("WebRTC is unavailable.", HttpStatusCode.ServiceUnavailable);
            case "/getJoinDetails": return GetJoinDetails(req);
            case "/allocate_ws": return await AllocateWebSockets(req);
            case "/migration/claim": return ClaimMigration(req);
            case "/migration/current": return GetMigrationCurrent(req);
            case "/getTotalConnections": return GetTotalConnections(req);
            default:
                return new ApiResponse(HttpStatusCode.NotFound);
        }
    }

    private static ApiResponse ClaimMigration(HttpRequestBase req)
    {
        var name = req.RetrieveHeaderValue("name");
        var internalSec = req.RetrieveHeaderValue("internal_key_secret");
        var claimSecret = req.RetrieveHeaderValue("migration_secret") ??
                          req.RetrieveHeaderValue("client_secret") ??
                          req.RetrieveHeaderValue("host_secret") ??
                          req.RetrieveHeaderValue("secret");
        var promotedPlayerId = req.RetrieveHeaderValue("promoted_player_id") ??
                               req.RetrieveHeaderValue("promoted_player");
        var expectedGeneration = ParseExpectedGeneration(req);

        if (string.IsNullOrWhiteSpace(name))
            throw new Exception("Missing name");

        if (string.IsNullOrWhiteSpace(internalSec))
            throw new Exception("Bad internal secret, -1");

        if (!string.Equals(internalSec, Program.SECRET_INTERNAL))
            throw new Exception($"Bad internal secret, {internalSec.Length}");

        if (string.IsNullOrWhiteSpace(claimSecret))
            throw new Exception("Missing migration secret");

        if (webServer == null || udpServerV1 == null)
            throw new Exception("No rooms available");

        var snapshot = Lobby.ClaimMigration(name, claimSecret, promotedPlayerId, expectedGeneration);
        return new ApiResponse(JObject.FromObject(ToMigrationJoinInfo(snapshot, snapshot.hostSecret)));
    }

    private static ApiResponse GetMigrationCurrent(HttpRequestBase req)
    {
        var name = req.RetrieveHeaderValue("name");

        if (string.IsNullOrWhiteSpace(name))
            throw new Exception("Missing name");

        if (webServer == null || udpServerV1 == null)
            throw new Exception("No rooms available");

        if (!Lobby.TryGetMigrationState(name, out var snapshot))
            throw new Exception("Room not found");

        return new ApiResponse(JObject.FromObject(ToMigrationJoinInfo(snapshot, snapshot.clientSecret)));
    }

    private static async Task<ApiResponse> AllocateWebSockets(HttpRequestBase req)
    {
        var name = req.RetrieveHeaderValue("name");
        var region = req.RetrieveHeaderValue("region");
        var internalSec = req.RetrieveHeaderValue("internal_key_secret");

        if (string.IsNullOrWhiteSpace(name))
            throw new Exception("Missing name");

        if (string.IsNullOrWhiteSpace(region))
            throw new Exception("Missing region");

        if (string.IsNullOrWhiteSpace(internalSec))
            throw new Exception("Bad internal secret, -1");

        if (!string.Equals(internalSec, Program.SECRET_INTERNAL))
            throw new Exception($"Bad internal secret, {internalSec.Length}");

        var secret = await Lobby.CreateRoom(region, name);

        webServer ??= new WebSockets(6942);
        udpServerV1 ??= UdpServerFactory.CreateV1(Program.UDP_PORT, CreateCallbacks(1));
        udpServerV2 ??= UdpServerFactory.CreateV2(Program.UDP_PORT_V2, CreateCallbacks(2));

        bool ssl = Env.TryGetValueOrDefault("HOST_SSL", "false") == "true";

        return new ApiResponse(JObject.FromObject(new ClientJoinInfo
        {
            ssl = ssl,
            port = webServer.port,
            secret = secret,
            udpPort = Program.UDP_PORT,
            udpPortV2 = Program.UDP_PORT_V2,
            webRtcUrl = webRtcRuntime?.PublicUrl
        }));
    }

    private static ApiResponse GetJoinDetails(HttpRequestBase req)
    {
        var name = req.RetrieveHeaderValue("name");

        if (string.IsNullOrWhiteSpace(name))
            throw new Exception("Missing name");

        if (webServer == null || udpServerV1 == null)
            throw new Exception("No rooms available");

        if (!Lobby.TryGetRoom(name, out var room) || room == null)
            throw new Exception("Room not found");

        var ssl = Env.TryGetValueOrDefault("HOST_SSL", "false") == "true";

        return new ApiResponse(JObject.FromObject(new ClientJoinInfo
        {
            ssl = ssl,
            port = webServer.port,
            secret = room.clientSecret,
            udpPort = Program.UDP_PORT,
            udpPortV2 = Program.UDP_PORT_V2,
            webRtcUrl = webRtcRuntime?.PublicUrl
        }));
    }

    private static ApiResponse GetTotalConnections(HttpRequestBase req)
    {
        var totalConnections = Transport.GetTotalConnectionCount();

        return new ApiResponse(JObject.FromObject(new
        {
            totalConnections = totalConnections
        }));
    }

    private static int? ParseExpectedGeneration(HttpRequestBase req)
    {
        var generation = req.RetrieveHeaderValue("expected_generation") ??
                         req.RetrieveHeaderValue("previous_generation");

        if (string.IsNullOrWhiteSpace(generation))
            return null;

        if (!int.TryParse(generation, out var parsed))
            throw new Exception("Invalid expected generation");

        return parsed;
    }

    private static MigrationJoinInfo ToMigrationJoinInfo(MigrationRoomSnapshot snapshot, string? secret)
    {
        var ssl = Env.TryGetValueOrDefault("HOST_SSL", "false") == "true";

        return new MigrationJoinInfo
        {
            ssl = ssl,
            port = webServer?.port ?? 0,
            secret = secret,
            udpPort = Program.UDP_PORT,
            udpPortV2 = Program.UDP_PORT_V2,
            webRtcUrl = webRtcRuntime?.PublicUrl,
            roomName = snapshot.name,
            generation = snapshot.generation,
            fencingToken = snapshot.fencingToken,
            promotedPlayerId = snapshot.promotedPlayerId,
            claimedAt = snapshot.claimedAt?.ToString("O", CultureInfo.InvariantCulture)
        };
    }
}

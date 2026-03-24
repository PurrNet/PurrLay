using System.Globalization;
using System.Net;
using System.Text;
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

    /// <summary>
    /// Maps global connection IDs to which UDP version they connected through.
    /// 1 = V1 (LiteNetLib 1.x), 2 = V2 (LiteNetLib 2.x).
    /// </summary>
    static readonly Dictionary<int, int> _connToUdpVersion = new();
    static readonly object _versionLock = new();

    static UdpServerCallbacks CreateCallbacks(int version) => new()
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
        return version == 2 ? udpServerV2 : udpServerV1;
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

    public static async Task RegisterRoom(string region, string roomName)
    {
        if (!Env.TryGetValue("BALANCER_URL", out var balancerUrl))
            throw new Exception("Missing `BALANCER_URL` env variable");

        using HttpClient client = new();

        client.DefaultRequestHeaders.Add("name", roomName);
        client.DefaultRequestHeaders.Add("region", region);
        client.DefaultRequestHeaders.Add("internal_key_secret", Program.SECRET_INTERNAL);

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{balancerUrl}/registerRoom"));

        if (!response.IsSuccessStatusCode)
        {
            var content = response.Content.ReadAsByteArrayAsync();
            var contentStr = Encoding.UTF8.GetString(content.Result);
            throw new Exception(contentStr);
        }
    }

    public static async Task unegisterRoom(string roomName)
    {
        if (!Env.TryGetValue("BALANCER_URL", out var balancerUrl))
            throw new Exception("Missing `BALANCER_URL` env variable");

        using HttpClient client = new();

        client.DefaultRequestHeaders.Add("name", roomName);
        client.DefaultRequestHeaders.Add("internal_key_secret", Program.SECRET_INTERNAL);

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{balancerUrl}/unregisterRoom"));

        if (!response.IsSuccessStatusCode)
        {
            var content = response.Content.ReadAsByteArrayAsync();
            var contentStr = Encoding.UTF8.GetString(content.Result);
            throw new Exception(contentStr);
        }
    }

    public static async Task updateConnectionCount(string roomName, int newCount)
    {
        if (!Env.TryGetValue("BALANCER_URL", out var balancerUrl))
            throw new Exception("Missing `BALANCER_URL` env variable");

        using HttpClient client = new();

        client.DefaultRequestHeaders.Add("name", roomName);
        client.DefaultRequestHeaders.Add("internal_key_secret", Program.SECRET_INTERNAL);
        client.DefaultRequestHeaders.Add("count", newCount.ToString());

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{balancerUrl}/updateConnectionCount"));

        if (!response.IsSuccessStatusCode)
        {
            var content = response.Content.ReadAsByteArrayAsync();
            var contentStr = Encoding.UTF8.GetString(content.Result);
            throw new Exception(contentStr);
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
    }

    public static async Task<ApiResponse> OnRequest(HttpRequestBase req)
    {
        if (req.Url == null)
            throw new Exception("Invalid URL");

        string path = req.Url.RawWithoutQuery;

        switch (path)
        {
            case "/": return new ApiResponse(DateTime.Now.ToString(CultureInfo.InvariantCulture));
            case "/ping": return new ApiResponse(HttpStatusCode.OK);
            case "/getJoinDetails": return GetJoinDetails(req);
            case "/allocate_ws": return await AllocateWebSockets(req);
            case "/getTotalConnections": return GetTotalConnections(req);
            default:
                return new ApiResponse(HttpStatusCode.NotFound);
        }
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
        udpServerV1 ??= new UdpServerV1(Program.UDP_PORT, CreateCallbacks(1));
        udpServerV2 ??= new UdpServerV2(Program.UDP_PORT_V2, CreateCallbacks(2));

        bool ssl = Env.TryGetValueOrDefault("HOST_SSL", "false") == "true";

        return new ApiResponse(JObject.FromObject(new ClientJoinInfo
        {
            ssl = ssl,
            port = webServer.port,
            secret = secret,
            udpPort = Program.UDP_PORT,
            udpPortV2 = Program.UDP_PORT_V2
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
            udpPortV2 = Program.UDP_PORT_V2
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
}

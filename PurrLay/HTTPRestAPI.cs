using System.Globalization;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using PurrCommon;
using WatsonWebserver.Core;
using HttpMethod = System.Net.Http.HttpMethod;

namespace PurrLay;

public static class HTTPRestAPI
{
    public static WebSockets? webServer;
    public static UdpServer? udpServer;

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
            var contentStr = await response.Content.ReadAsStringAsync();
            throw new Exception(contentStr);
        }
    }

    public static async Task UnregisterRoom(string roomName)
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
            var contentStr = await response.Content.ReadAsStringAsync();
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
        udpServer ??= new UdpServer(Program.UDP_PORT);

        bool ssl = Env.TryGetValueOrDefault("HOST_SSL", "false") == "true";

        return new ApiResponse(JObject.FromObject(new ClientJoinInfo
        {
            ssl = ssl,
            port = webServer.port,
            secret = secret,
            udpPort = Program.UDP_PORT
        }));
    }

    private static ApiResponse GetJoinDetails(HttpRequestBase req)
    {
        var name = req.RetrieveHeaderValue("name");

        if (string.IsNullOrWhiteSpace(name))
            throw new Exception("Missing name");

        if (webServer == null || udpServer == null)
            throw new Exception("No rooms available");

        if (!Lobby.TryGetRoom(name, out var room) || room == null)
            throw new Exception("Room not found");

        var ssl = Env.TryGetValueOrDefault("HOST_SSL", "false") == "true";

        return new ApiResponse(JObject.FromObject(new ClientJoinInfo
        {
            ssl = ssl,
            port = webServer.port,
            secret = room.clientSecret,
            udpPort = Program.UDP_PORT
        }));
    }
}

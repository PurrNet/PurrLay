using System.Globalization;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using WatsonWebserver.Core;
using PurrCommon;
using HttpMethod = System.Net.Http.HttpMethod;

namespace PurrBalancer;

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
                        lock (_relayServers)
                        {
                            for (var i = index; i < relayCount; i++)
                            {
                                if (_relayServers[i].apiEndpoint == endpoint)
                                {
                                    _relayServers.RemoveAt(i);
                                    index--;
                                    break;
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

    static readonly Dictionary<string, string> _roomToRegion = new();

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
            case "/join":
                return await HandleJoin(req);
            case "/allocate_ws":
                return await AllocateRoom(req);
            default:
                return new ApiResponse(HttpStatusCode.NotFound);
        }
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
            var contentStr = await resp.Content.ReadAsStringAsync();
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

        if (!_roomToRegion.TryGetValue(name, out var region))
            throw new Exception("PurrBalancer: Room not found");

        if (!TryGetServer(region, out var server))
            throw new Exception("PurrBalancer: Invalid region");

        using HttpClient client = new();

        client.DefaultRequestHeaders.Add("name", name);
        client.DefaultRequestHeaders.Add("region", region);
        client.DefaultRequestHeaders.Add("internal_key_secret", Program.SECRET_INTERNAL);

        var r = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{server.apiEndpoint}/getJoinDetails"));

        if (!r.IsSuccessStatusCode)
        {
            var contentStr = await r.Content.ReadAsStringAsync();
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

        if (!_roomToRegion.Remove(name, out _))
            throw new Exception("PurrBalancer: Room not found");

        return new ApiResponse(new JObject
        {
            ["status"] = "ok"
        });
    }

    private static ApiResponse RegisterRoom(HttpRequestBase req)
    {
        var region = req.RetrieveHeaderValue("region");
        var name = req.RetrieveHeaderValue("name");
        var internalSecret = req.RetrieveHeaderValue("internal_key_secret");

        if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(internalSecret))
            throw new Exception("PurrBalancer_registerRoom: Invalid headers");

        if (!string.Equals(internalSecret, Program.SECRET_INTERNAL))
            throw new Exception("PurrBalancer: Invalid internal secret");

        if (!TryGetServer(region, out _))
            throw new Exception("PurrBalancer: Invalid region when registering room");

        if (!_roomToRegion.TryAdd(name, region))
            throw new Exception("PurrBalancer: Room already registered");

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
                if (_relayServers[i].region == server.region)
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

        lock (_relayServers)
        {
            for (var i = 0; i < _relayServers.Count; i++)
            {
                if (_relayServers[i].host == server.host)
                    _relayServers.RemoveAt(i);
            }
        }

        return new ApiResponse(new JObject
        {
            ["status"] = "ok"
        });
    }
}

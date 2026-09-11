extern alias balancer;

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;
using Api = balancer::PurrBalancer.HTTPRestAPI;
using Response = balancer::PurrBalancer.ApiResponse;

namespace PurrLay.Tests;

public sealed class BalancerDeploymentTests
{
    [Fact]
    public async Task ActivationKeepsOldRoomOwnerAndFencesStaleHeartbeats()
    {
        using var state = new StateScope();
        var region = Guid.NewGuid().ToString("N");
        var oldServer = Server("http://old.example", region, "old-process", "old-release");
        var nextServer = Server("http://next.example", region, "next-process", "next-release", true);
        await OkAsync(Request("/registerServer", oldServer));
        await OkAsync(Room("/registerRoom", "existing", region, "http://old.example", "room", "old-process"));
        await OkAsync(Request("/registerServer", nextServer));
        await OkAsync(Request("/admin/activateRelay", new JObject { ["apiEndpoint"] = "http://next.example", ["instanceId"] = "next-process" }));

        var stale = await OkAsync(Request("/registerServer", oldServer));
        Assert.False(stale.Value<bool>("acceptingRooms"));
        Assert.Equal("http://old.example", Snapshot()["roomToServerEndpoint"]!["existing"]!.Value<string>());
        var available = (await OkAsync(Request("/servers")))["servers"]!.Children<JObject>().Where(item => item.Value<string>("region") == region).ToArray();
        Assert.Single(available);
        Assert.Equal("http://next.example", available[0].Value<string>("apiEndpoint"));

        nextServer["instanceId"] = "restarted-process";
        var restarted = await OkAsync(Request("/registerServer", nextServer));
        Assert.True(restarted.Value<bool>("acceptingRooms"));
        Assert.Equal("restarted-process", restarted.Value<string>("instanceId"));
        Assert.Equal("next-release", restarted.Value<string>("deploymentId"));
    }

    [Fact]
    public async Task StaleActivationAndCompetingHandoffCannotChangeAuthority()
    {
        using var state = new StateScope();
        await OkAsync(Request("/registerServer", Server("http://relay.example", "region", "current", "release", true)));
        var stale = await Api.OnRequest(Request("/admin/activateRelay", new JObject { ["apiEndpoint"] = "http://relay.example", ["instanceId"] = "stale" }));
        Assert.Equal(HttpStatusCode.Conflict, stale.status);
        var first = new JObject { ["successorUrl"] = "http://successor.example", ["successorInstanceId"] = "next" };
        var prepared = await OkAsync(Request("/admin/handoff", first));
        Assert.NotNull(prepared["handoffId"]);
        var competing = await Api.OnRequest(Request("/admin/handoff", new JObject { ["successorUrl"] = "http://other.example", ["successorInstanceId"] = "other" }));
        Assert.Equal(HttpStatusCode.Conflict, competing.status);
        first["handoffId"] = "wrong-token";
        Assert.Equal(HttpStatusCode.Conflict, (await Api.OnRequest(Request("/admin/commitHandoff", first))).status);
        Assert.True((await OkAsync(Request("/admin/status"))).Value<bool>("ready"));
    }

    [Fact]
    public async Task FinalHandoffIncludesInterveningMutationsAndForwardsLateRequests()
    {
        using var state = new StateScope();
        var forwarded = new List<string>();
        await using var successor = new FakeApi(request =>
        {
            lock (forwarded) forwarded.Add(request.Url!.AbsolutePath);
            return Task.FromResult((200, new JObject { ["status"] = "ok" }));
        });
        await OkAsync(Request("/registerServer", Server("http://relay.example", "region", "process", "release")));
        await OkAsync(Room("/registerRoom", "room", "region", "http://relay.example", "first", "process"));
        var handoff = new JObject { ["successorUrl"] = successor.Endpoint, ["successorInstanceId"] = "next" };
        var initial = await OkAsync(Request("/admin/handoff", handoff));
        var update = Room("/updateConnectionCount", "room", "region", "http://relay.example", "first", "process");
        update.Headers["count"] = "7"; update.Headers["count_sequence"] = "5";
        await OkAsync(update);
        handoff["handoffId"] = initial["handoffId"];
        var final = await OkAsync(Request("/admin/commitHandoff", handoff));
        Assert.Equal(7, final["snapshot"]!["rooms"]!.Single(room => room.Value<string>("name") == "room").Value<int>("connectedPlayers"));
        Assert.Equal(5, final["snapshot"]!["roomCountSequences"]!["room"]!.Value<long>());
        Assert.Equal(0, initial["snapshot"]!["rooms"]!.Single(room => room.Value<string>("name") == "room").Value<int>("connectedPlayers"));
        await OkAsync(Room("/unregisterRoom", "room", "region", "http://relay.example", "first", "process"));
        Assert.Contains("/unregisterRoom", forwarded);
        Assert.NotNull(Snapshot()["roomToServerEndpoint"]!["room"]);
        var repeat = await OkAsync(Request("/admin/commitHandoff", handoff));
        Assert.True(JToken.DeepEquals(final, repeat));
        var readiness = Request("/admin/ready"); readiness.Headers.Clear();
        Assert.Equal(HttpStatusCode.OK, (await Api.OnRequest(readiness)).status);
    }

    [Fact]
    public async Task PendingStartupCheckpointKeepsIdentityAndNeverBecomesAnEmptyAuthority()
    {
        using var state = new StateScope();
        var directory = Path.Combine(Path.GetTempPath(), "balancer-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        try
        {
            state.Environment("BALANCER_STATE_PATH", path);
            state.Environment("BALANCER_SELF_URL", "http://successor.example");
            state.Environment("BALANCER_PREDECESSOR_URL", "http://source.example");
            Call("ConfigureDeployment");
            var first = JObject.Parse(File.ReadAllText(path));
            Assert.Equal("initializing", first.Value<string>("phase"));
            Assert.Equal("http://source.example", first.Value<string>("pendingPredecessor"));
            Set("_pendingHandoffId", "saved-token");
            Call("PersistCheckpoint");
            Set("_instanceId", "different-process"); Set("_pendingHandoffId", null);
            Call("LoadCheckpoint");
            Assert.Equal(first.Value<string>("instanceId"), Get<string>("_instanceId"));
            Assert.Equal("saved-token", Get<string>("_pendingHandoffId"));
            Assert.False(Get<bool>("_ready"));
            Assert.False(Get<bool>("_checkpointLoaded"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Api.OnRequest(Request("/admin/ready"))).status);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task CorruptCheckpointFailsReadinessInsteadOfBootstrappingEmptyState()
    {
        using var state = new StateScope();
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not-json");
            state.Environment("BALANCER_STATE_PATH", path);
            state.Environment("BALANCER_PREDECESSOR_URL", "");
            state.Environment("BALANCER_SELF_URL", "http://self.example");
            Call("ConfigureDeployment");
            await (Task)Call("InitializeDeploymentAsync")!;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Api.OnRequest(Request("/admin/ready"))).status);
            Assert.Contains("checkpoint", (await OkAsync(Request("/admin/status"))).Value<string>("startupError"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LegacyBootstrapKeepsOldAuthorityAndUsesItsCasForNewRoomNames()
    {
        using var state = new StateScope();
        var oldEndpoint = "http://old-relay.example";
        var servers = new Dictionary<string, JObject> { [oldEndpoint] = Server(oldEndpoint, "legacy-region", "old", null) };
        var rooms = new Dictionary<string, (string endpoint, string instance, int count)>
        {
            ["legacy-room"] = (oldEndpoint, "legacy-instance", 2)
        };
        await using var legacy = new FakeApi(async request =>
        {
            using var reader = new StreamReader(request.InputStream);
            var text = await reader.ReadToEndAsync();
            var name = request.Headers["name"] ?? "";
            var endpoint = request.Headers["relay_endpoint"] ?? "";
            var instance = request.Headers["room_instance_id"] ?? "";
            lock (rooms)
            {
                switch (request.Url!.AbsolutePath)
                {
                    case "/admin/status": return (404, new JObject());
                    case "/servers": return (200, new JObject { ["servers"] = JArray.FromObject(servers.Values) });
                    case "/list": return (200, new JObject { ["total"] = rooms.Count, ["results"] = new JArray(rooms.Select(pair => new JObject
                    {
                        ["name"] = pair.Key, ["region"] = "legacy-region", ["connectedPlayers"] = pair.Value.count
                    })) });
                    case "/join": return rooms.ContainsKey(name)
                        ? (200, new JObject { ["host"] = "old-host", ["port"] = 6942, ["secret"] = "preserved" })
                        : (404, new JObject());
                    case "/registerServer":
                        var server = JObject.Parse(text); servers[server.Value<string>("apiEndpoint")!] = server;
                        return (200, new JObject { ["status"] = "ok" });
                    case "/registerRoom":
                        if (rooms.TryGetValue(name, out var owner) && (owner.endpoint != endpoint || owner.instance != instance))
                            return (409, new JObject { ["error"] = "Room already registered" });
                        rooms.TryAdd(name, (endpoint, instance, 0));
                        return (200, new JObject { ["status"] = "ok" });
                    case "/updateConnectionCount":
                        if (rooms.TryGetValue(name, out var current) && current.endpoint == endpoint && current.instance == instance)
                            rooms[name] = (endpoint, instance, int.Parse(request.Headers["count"]!));
                        return (200, new JObject { ["status"] = "ok" });
                    default: return (404, new JObject());
                }
            }
        });
        state.Environment("BALANCER_PREDECESSOR_URL", legacy.Endpoint);
        state.Environment("BALANCER_SELF_URL", "http://new-balancer.example");
        state.Environment("BALANCER_STATE_PATH", "");
        Call("ConfigureDeployment");
        await (Task)Call("InitializeDeploymentAsync")!;
        Assert.True(Get<bool>("_ready"));
        var oldJoin = await OkAsync(Room("/join", "legacy-room", "legacy-region", oldEndpoint, "legacy-instance", "old"));
        Assert.Equal("preserved", oldJoin.Value<string>("secret"));
        Assert.Null(Snapshot()["roomInstanceIds"]!["legacy-room"]);

        var newEndpoint = "http://new-relay.example";
        await OkAsync(Request("/registerServer", Server(newEndpoint, "legacy-region", "new", "release", true)));
        var duplicate = await Api.OnRequest(Room("/registerRoom", "legacy-room", "legacy-region", newEndpoint, "new-instance", "new"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.status);
        Assert.Null(Snapshot()["roomInstanceIds"]!["legacy-room"]);
        var register = Room("/registerRoom", "new-room", "legacy-region", newEndpoint, "new-instance", "new");
        await OkAsync(register);
        await OkAsync(register);
        var count = Room("/updateConnectionCount", "new-room", "legacy-region", newEndpoint, "new-instance", "new");
        count.Headers["count"] = "3"; count.Headers["count_sequence"] = "1";
        await OkAsync(count);
        Assert.Equal(3, rooms["new-room"].count);
        var listed = (await OkAsync(Request("/list")))["results"]!;
        Assert.Single(listed, room => room.Value<string>("name") == "new-room");
        Assert.Contains(listed, room => room.Value<string>("name") == "legacy-room");
    }

    [Fact]
    public async Task AmbiguousLegacyRegionDoesNotPassReadiness()
    {
        using var state = new StateScope();
        await using var legacy = new FakeApi(request => Task.FromResult(request.Url!.AbsolutePath == "/admin/status"
            ? (404, new JObject())
            : (200, new JObject { ["servers"] = new JArray(Server("http://one.example", "same", "one", null), Server("http://two.example", "same", "two", null)) })));
        state.Environment("BALANCER_PREDECESSOR_URL", legacy.Endpoint);
        state.Environment("BALANCER_SELF_URL", "http://new.example");
        state.Environment("BALANCER_STATE_PATH", "");
        Call("ConfigureDeployment");
        await (Task)Call("InitializeDeploymentAsync")!;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Api.OnRequest(Request("/admin/ready"))).status);
        Assert.Contains("ambiguous", (await OkAsync(Request("/admin/status"))).Value<string>("startupError"));
    }

    [Fact]
    public async Task PublicServersPreserveRegionalEndpointAndHostForLatencySelection()
    {
        using var state = new StateScope();
        state.Environment("BALANCER_PUBLIC_URL", "https://central-balancer.example");
        const string endpoint = "https://regional-relay.example:20003";
        const string region = "direct-latency-region";
        await OkAsync(Request("/registerServer", Server(endpoint, region, "regional-instance", "release")));

        var servers = (await OkAsync(Request("/servers")))["servers"]!;
        var server = Assert.Single(servers, item => item.Value<string>("region") == region);
        Assert.Equal(endpoint, server.Value<string>("apiEndpoint"));
        Assert.Equal("cached-host", server.Value<string>("host"));
    }

    [Fact]
    public async Task PublicRelayProxySupportsPreflightAndBoundsOfferBodies()
    {
        using var state = new StateScope();
        var forwarded = 0;
        var expectedPath = "/webrtc/offer";
        var offer = new JObject { ["type"] = "offer", ["sdp"] = "test-sdp" };
        await using var relay = new FakeApi(async request =>
        {
            ++forwarded;
            Assert.Equal(expectedPath, request.Url!.AbsolutePath);
            Assert.Equal("application/json", request.ContentType);
            using var reader = new StreamReader(request.InputStream);
            Assert.True(JToken.DeepEquals(offer, JObject.Parse(await reader.ReadToEndAsync())));
            return (503, new JObject { ["error"] = "gateway unavailable" });
        });
        await OkAsync(Request("/registerServer", Server(relay.Endpoint, "proxy-region", "proxy-instance", "release")));
        var preflight = Request("/relay/proxy-instance/webrtc/offer");
        preflight.Headers.Clear(); preflight.Method = WatsonWebserver.Core.HttpMethod.OPTIONS;
        Assert.Equal(HttpStatusCode.NoContent, (await Api.OnRequest(preflight)).status);
        Assert.Equal(0, forwarded);

        var request = Request("/relay/proxy-instance/webrtc/offer", offer);
        request.Headers.Clear();
        var response = await Api.OnRequest(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.status);
        Assert.Equal("gateway unavailable", JObject.Parse(Encoding.UTF8.GetString(response.data)).Value<string>("error"));
        Assert.Equal(1, forwarded);

        var oversized = Request("/relay/proxy-instance/webrtc/offer", offer);
        oversized.Headers.Clear();
        oversized.Data = new MemoryStream(new byte[1024 * 1024]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Api.OnRequest(oversized)).status);
        Assert.Equal(128 * 1024 + 1, oversized.Data.Position);
        Assert.Equal(1, forwarded);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.OnRequest(Request("/relay/unknown/webrtc/offer", offer))).status);

        Set("_successorUrl", relay.Endpoint);
        oversized.Data.Position = 0;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Api.OnRequest(oversized)).status);
        Assert.Equal(128 * 1024 + 1, oversized.Data.Position);
        Assert.Equal(1, forwarded);
        expectedPath = "/relay/proxy-instance/webrtc/offer";
        var forwardedOffer = Request(expectedPath, offer);
        forwardedOffer.Headers["Content-Type"] = "application/json";
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Api.OnRequest(forwardedOffer)).status);
        Assert.Equal(2, forwarded);
    }

    private static JObject Server(string endpoint, string region, string instance, string? deployment, bool draining = false) => new()
    {
        ["apiEndpoint"] = endpoint, ["region"] = region, ["host"] = "cached-host",
        ["instanceId"] = instance, ["deploymentId"] = deployment, ["draining"] = draining
    };

    private static TestHttpRequest Request(string path, JObject? body = null)
    {
        var request = new TestHttpRequest(path, ("internal_key_secret", "PURRNET"));
        if (body != null)
        {
            request.Method = WatsonWebserver.Core.HttpMethod.POST;
            request.Data = new MemoryStream(Encoding.UTF8.GetBytes(body.ToString()));
        }
        return request;
    }

    private static TestHttpRequest Room(string path, string name, string region, string endpoint, string roomInstance, string relayInstance) =>
        new(path, ("internal_key_secret", "PURRNET"), ("name", name), ("region", region),
            ("relay_endpoint", endpoint), ("room_instance_id", roomInstance), ("relay_instance_id", relayInstance));

    private static async Task<JObject> OkAsync(TestHttpRequest request)
    {
        var response = await Api.OnRequest(request);
        Assert.Equal(HttpStatusCode.OK, response.status);
        return JObject.Parse(Encoding.UTF8.GetString(response.data));
    }

    private static object? Call(string name, params object[] arguments) => typeof(Api)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);
    private static T Get<T>(string name) => (T)typeof(Api).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static void Set(string name, object? value) => typeof(Api).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
    private static JObject Snapshot() => (JObject)Call("CaptureSnapshot")!;

    private sealed class StateScope : IDisposable
    {
        private readonly JObject _snapshot = Snapshot();
        private readonly Dictionary<FieldInfo, object?> _fields = typeof(Api).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => !field.IsInitOnly).ToDictionary(field => field, field => field.GetValue(null));
        private readonly Dictionary<string, string?> _environment = new();

        public StateScope()
        {
            Set("_ready", true); Set("_successorUrl", null); Set("_successorInstanceId", null);
            Set("_handoffId", null); Set("_handoffTarget", null); Set("_handoffInstance", null);
            Set("_committedSnapshot", null); Set("_checkpointPath", null); Set("_checkpointFailed", false);
            Set("_checkpointLoaded", false); Set("_initializing", false); Set("_pendingPredecessor", null);
            Set("_pendingHandoffId", null); Set("_startupError", null); Set("_selfUrl", "http://source.example");
        }

        public void Environment(string key, string value)
        {
            _environment.TryAdd(key, System.Environment.GetEnvironmentVariable(key));
            System.Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var entry in _environment) System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            Call("RestoreSnapshot", _snapshot);
            foreach (var entry in _fields) entry.Key.SetValue(null, entry.Value);
        }
    }

    private sealed class FakeApi : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly Func<HttpListenerRequest, Task<(int status, JObject body)>> _handler;
        public string Endpoint { get; }

        public FakeApi(Func<HttpListenerRequest, Task<(int status, JObject body)>> handler)
        {
            _handler = handler;
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start(); Endpoint = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}"; port.Stop();
            _listener.Prefixes.Add(Endpoint + "/"); _listener.Start(); _loop = RunAsync();
        }

        private async Task RunAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { break; }
                try
                {
                    var result = await _handler(context.Request);
                    var bytes = Encoding.UTF8.GetBytes(result.body.ToString());
                    context.Response.StatusCode = result.status;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                finally { context.Response.Close(); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop(); await _loop; _listener.Close();
        }
    }
}

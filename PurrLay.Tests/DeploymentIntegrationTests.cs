using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace PurrLay.Tests;

public sealed class DeploymentIntegrationTests
{
    [Fact]
    public async Task LiveRoomAndLateJoinSurviveBalancerHandoffAndRelayReplacement()
    {
        await using var edge = new TestEdge();
        await using var firstBalancer = Child.Balancer("first", edge.Endpoint);
        edge.Target = firstBalancer.Endpoint;
        await firstBalancer.WaitReadyAsync();
        await using var oldRelay = Child.Relay("old", edge.Endpoint);
        var oldStatus = await oldRelay.WaitReadyAsync();
        await ActivateAsync(edge.Endpoint, oldRelay, oldStatus);

        var oldName = "old-room-" + Guid.NewGuid().ToString("N");
        var allocation = await JsonAsync(edge.Endpoint, "/allocate_ws", name: oldName, region: "integration");
        Assert.Equal(oldRelay.WebSocketPort, (int)allocation["port"]!);
        using var host = await ConnectAsync(oldRelay.WebSocketPort, oldName, (string)allocation["secret"]!);
        var join = await JsonAsync(edge.Endpoint, "/join", name: oldName);
        using var client = await ConnectAsync((int)join["port"]!, oldName, (string)join["secret"]!);
        var clientId = await ConnectedIdAsync(host);
        await ExchangeAsync(host, client, clientId);

        await using var successor = Child.Balancer("successor", edge.Endpoint, firstBalancer.Endpoint);
        await successor.WaitReadyAsync();
        edge.Target = successor.Endpoint;
        var forwardedJoin = await JsonAsync(firstBalancer.Endpoint, "/join", name: oldName);
        Assert.Equal(oldRelay.WebSocketPort, (int)forwardedJoin["port"]!);
        Assert.True((bool)(await JsonAsync(firstBalancer.Endpoint, "/admin/status"))["canRetire"]!);
        await firstBalancer.StopAsync();

        await using var newRelay = Child.Relay("new", edge.Endpoint);
        var newStatus = await newRelay.WaitReadyAsync();
        await ActivateAsync(edge.Endpoint, newRelay, newStatus);
        await JsonAsync(oldRelay.Endpoint, "/admin/drain", post: true, instance: (string)oldStatus["instanceId"]!);

        var busy = await StatusAsync(oldRelay);
        Assert.True((bool)busy["draining"]!);
        Assert.False((bool)busy["canRetire"]!);
        using (var denied = await RequestAsync(oldRelay.Endpoint, "/admin/retire", post: true,
                   instance: (string)oldStatus["instanceId"]!))
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);

        join = await JsonAsync(edge.Endpoint, "/join", name: oldName);
        Assert.Equal(oldRelay.WebSocketPort, (int)join["port"]!);
        Assert.Equal("127.0.0.1", (string)join["host"]!);
        using var lateJoin = await ConnectAsync((int)join["port"]!, oldName, (string)join["secret"]!);
        var lateId = await ConnectedIdAsync(host);
        await ExchangeAsync(host, lateJoin, lateId);
        await ExchangeAsync(host, client, clientId);

        await successor.StopAsync();
        await using var restartedBalancer = Child.Balancer("successor-restart", edge.Endpoint,
            firstBalancer.Endpoint, successor.StatePath);
        await restartedBalancer.WaitReadyAsync();
        edge.Target = restartedBalancer.Endpoint;
        Assert.Equal(oldRelay.WebSocketPort, (int)(await JsonAsync(edge.Endpoint, "/join", name: oldName))["port"]!);
        await ExchangeAsync(host, client, clientId);

        var newName = "new-room-" + Guid.NewGuid().ToString("N");
        var replacementAllocation = await JsonAsync(edge.Endpoint, "/allocate_ws", name: newName, region: "integration");
        Assert.Equal(newRelay.WebSocketPort, (int)replacementAllocation["port"]!);
        Assert.NotEqual(oldRelay.WebSocketPort, newRelay.WebSocketPort);
        // Older hosts keep the same cached hostname and use the returned port.
        using var replacementHost = await ConnectAsync(newRelay.WebSocketPort, newName,
            (string)replacementAllocation["secret"]!);
        var listed = (await JsonAsync(edge.Endpoint, "/list"))["results"]!.Children<JObject>().ToArray();
        Assert.Contains(listed, item => (string?)item["name"] == oldName);
        Assert.Contains(listed, item => (string?)item["name"] == newName);

        await CloseAsync(lateJoin);
        await DisconnectedIdAsync(host, lateId);
        await CloseAsync(client);
        await DisconnectedIdAsync(host, clientId);
        await CloseAsync(host);
        await WaitAsync(async () => (bool)(await StatusAsync(oldRelay))["canRetire"]!);
        var retired = await JsonAsync(oldRelay.Endpoint, "/admin/retire", post: true,
            instance: (string)oldStatus["instanceId"]!);
        Assert.True((bool)retired["retired"]!);
        await oldRelay.StopAsync();

        var replacementJoin = await JsonAsync(edge.Endpoint, "/join", name: newName);
        using var replacementClient = await ConnectAsync((int)replacementJoin["port"]!, newName,
            (string)replacementJoin["secret"]!);
        var replacementId = await ConnectedIdAsync(replacementHost);
        await ExchangeAsync(replacementHost, replacementClient, replacementId);
        await CloseAsync(replacementClient);
        await DisconnectedIdAsync(replacementHost, replacementId);
        await CloseAsync(replacementHost);
    }

    static Task<JObject> StatusAsync(Child child) => JsonAsync(child.Endpoint, "/admin/status");

    static async Task ActivateAsync(string balancer, Child relay, JObject status)
    {
        var instance = (string)status["instanceId"]!;
        await JsonAsync(relay.Endpoint, "/admin/activate", post: true, instance: instance);
        await WaitAsync(async () =>
        {
            using var response = await RequestAsync(balancer, "/admin/activateRelay", post: true,
                body: new JObject { ["apiEndpoint"] = relay.Endpoint, ["instanceId"] = instance });
            return response.IsSuccessStatusCode;
        });
    }

    static async Task<JObject> JsonAsync(string endpoint, string path, bool post = false, string? name = null,
        string? region = null, string? instance = null, JObject? body = null)
    {
        using var response = await RequestAsync(endpoint, path, post, name, region, instance, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return JObject.Parse(text);
    }

    static async Task<HttpResponseMessage> RequestAsync(string endpoint, string path, bool post = false,
        string? name = null, string? region = null, string? instance = null, JObject? body = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, endpoint + path);
        request.Headers.Add("internal_key_secret", "PURRNET");
        if (name != null) request.Headers.Add("name", name);
        if (region != null) request.Headers.Add("region", region);
        if (instance != null) request.Headers.Add("relay_instance_id", instance);
        if (body != null) request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    static async Task<ClientWebSocket> ConnectAsync(int port, string room, string secret)
    {
        var socket = new ClientWebSocket();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), timeout.Token);
            await SendAsync(socket, Encoding.UTF8.GetBytes(new JObject
                { ["roomName"] = room, ["clientSecret"] = secret }.ToString()));
            Assert.Equal(new byte[] { 3 }, await ReceiveAsync(socket));
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    static async Task<int> ConnectedIdAsync(ClientWebSocket host)
    {
        var message = await ReceiveAsync(host);
        Assert.Equal(5, message.Length);
        Assert.Equal(0, message[0]);
        return BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(1));
    }

    static async Task DisconnectedIdAsync(ClientWebSocket host, int id)
    {
        var message = await ReceiveAsync(host);
        Assert.Equal(5, message.Length);
        Assert.Equal(1, message[0]);
        Assert.Equal(id, BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(1)));
    }

    static async Task ExchangeAsync(ClientWebSocket host, ClientWebSocket client, int id)
    {
        byte[] payload = [71, 72, 73, 74];
        await SendAsync(client, payload);
        var delivered = await ReceiveAsync(host);
        Assert.Equal(2, delivered[0]);
        Assert.Equal(id, BinaryPrimitives.ReadInt32LittleEndian(delivered.AsSpan(1)));
        Assert.Equal(payload, delivered[5..]);
        var reply = new byte[5 + payload.Length];
        reply[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(1), id);
        payload.CopyTo(reply, 5);
        await SendAsync(host, reply);
        Assert.Equal(payload, await ReceiveAsync(client));
    }

    static async Task SendAsync(ClientWebSocket socket, byte[] data)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, timeout.Token);
    }

    static async Task<byte[]> ReceiveAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.True(result.EndOfMessage);
        return buffer[..result.Count];
    }

    static async Task CloseAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token);
    }

    static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (!await predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for deployment transition.");
            await Task.Delay(40);
        }
    }

    static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    sealed class Child : IAsyncDisposable
    {
        readonly Process _process;
        readonly ConcurrentQueue<string> _output = new();
        readonly DirectoryInfo _workingDirectory = Directory.CreateTempSubdirectory("purrlay-deployment-test-");
        readonly bool _ownsState;
        public string Endpoint { get; }
        public int WebSocketPort { get; }
        public string? StatePath { get; }

        Child(string project, string deployment, string balancer, string? predecessor, string? statePath = null)
        {
            var port = Port();
            Endpoint = $"http://127.0.0.1:{port}";
            WebSocketPort = Port();
            var buildDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = buildDirectory.Parent!.Name;
            var repository = buildDirectory.Parent.Parent!.Parent!.Parent!.FullName;
            var binary = Path.Combine(repository, project, "bin", configuration, "net8.0", project + ".dll");
            Assert.True(File.Exists(binary), $"Missing application build: {binary}");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = _workingDirectory.FullName
            };
            start.ArgumentList.Add(binary);
            foreach (var name in new[] { "FLY_APP_NAME", "FLY_PROCESS_GROUP", "FLY_MACHINE_ID", "FLY_PRIVATE_IP",
                         "BALANCER_PREDECESSOR_URL", "BALANCER_SELF_URL", "BALANCER_DEPLOYMENT_ID", "RELAY_DEPLOYMENT_ID",
                         "BALANCER_STATE_PATH" })
                start.Environment.Remove(name);
            var environment = new Dictionary<string, string>
            {
                ["HOST"] = "127.0.0.1", ["PORT"] = port.ToString(), ["SECRET"] = "PURRNET",
                ["HOST_ENDPOINT"] = Endpoint, ["HOST_DOMAIN"] = "127.0.0.1", ["HOST_SSL"] = "false",
                ["HOST_REGION"] = "integration", ["BALANCER_URL"] = balancer,
                ["BALANCER_PUBLIC_URL"] = balancer, ["WEBRTC_ENABLED"] = "false",
                ["WEBSOCKETS_PORT"] = WebSocketPort.ToString(),
                ["UDP_PORT"] = Port().ToString(), ["UDP_PORT_V2"] = Port().ToString(),
                ["BALANCER_SELF_URL"] = Endpoint, ["BALANCER_DEPLOYMENT_ID"] = deployment,
                ["RELAY_DEPLOYMENT_ID"] = deployment, ["RELAY_START_STANDBY"] = "true"
            };
            if (predecessor != null) environment["BALANCER_PREDECESSOR_URL"] = predecessor;
            if (project == "PurrBalancer")
            {
                StatePath = statePath ?? Path.Combine(_workingDirectory.FullName, "balancer-state.json");
                _ownsState = statePath == null;
                environment["BALANCER_STATE_PATH"] = StatePath;
            }
            foreach (var (key, value) in environment) start.Environment[key] = value;
            _process = new Process { StartInfo = start };
            _process.OutputDataReceived += (_, args) => Capture(args.Data);
            _process.ErrorDataReceived += (_, args) => Capture(args.Data);
            Assert.True(_process.Start());
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public static Child Balancer(string deployment, string publicUrl, string? predecessor = null, string? statePath = null) =>
            new("PurrBalancer", deployment, publicUrl, predecessor, statePath);
        public static Child Relay(string deployment, string balancer) => new("PurrLay", deployment, balancer, null);

        void Capture(string? line)
        {
            if (line == null) return;
            _output.Enqueue(line);
            while (_output.Count > 150) _output.TryDequeue(out _);
        }

        public async Task<JObject> WaitReadyAsync()
        {
            JObject? status = null;
            try
            {
                await WaitAsync(async () =>
                {
                    Assert.False(_process.HasExited, string.Join('\n', _output));
                    try
                    {
                        using var response = await RequestAsync(Endpoint, "/admin/status");
                        if (!response.IsSuccessStatusCode) return false;
                        status = JObject.Parse(await response.Content.ReadAsStringAsync());
                        return (bool?)status["ready"] == true;
                    }
                    catch (HttpRequestException) { return false; }
                });
            }
            catch (Exception error) { throw new InvalidOperationException(string.Join('\n', _output), error); }
            return status!;
        }

        public async Task StopAsync()
        {
            if (_process.HasExited) return;
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _process.Dispose();
            if (_ownsState)
                foreach (var file in _workingDirectory.EnumerateFiles("balancer-state.json*")) file.Delete();
            if (!_workingDirectory.EnumerateFileSystemInfos().Any()) _workingDirectory.Delete();
        }
    }

    sealed class TestEdge : IAsyncDisposable
    {
        readonly HttpListener _listener = new();
        readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(15) };
        readonly ConcurrentBag<Task> _requests = new();
        readonly Task _accept;
        public string Target = "";
        public string Endpoint { get; } = $"http://127.0.0.1:{Port()}";

        public TestEdge()
        {
            _listener.Prefixes.Add(Endpoint + "/");
            _listener.Start();
            _accept = AcceptAsync();
        }

        async Task AcceptAsync()
        {
            try
            {
                while (_listener.IsListening) _requests.Add(ForwardAsync(await _listener.GetContextAsync()));
            }
            catch (HttpListenerException) when (!_listener.IsListening) { }
            catch (ObjectDisposedException) { }
        }

        async Task ForwardAsync(HttpListenerContext context)
        {
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod),
                    Volatile.Read(ref Target) + context.Request.RawUrl);
                foreach (var header in context.Request.Headers.AllKeys)
                    if (header is not null && !header.Equals("Host", StringComparison.OrdinalIgnoreCase))
                        request.Headers.TryAddWithoutValidation(header, context.Request.Headers[header]);
                if (context.Request.HasEntityBody)
                {
                    using var body = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(body);
                    request.Content = new ByteArrayContent(body.ToArray());
                }
                using var response = await _client.SendAsync(request);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception error)
            {
                if (_listener.IsListening)
                {
                    try
                    {
                        context.Response.StatusCode = 502;
                        var bytes = Encoding.UTF8.GetBytes(error.Message);
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    catch (ObjectDisposedException) { }
                    catch (HttpListenerException) { }
                }
            }
            finally
            {
                try { context.Response.Close(); }
                catch (ObjectDisposedException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            await _accept;
            await Task.WhenAll(_requests);
            _client.Dispose();
        }
    }
}

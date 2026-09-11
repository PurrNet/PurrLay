using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace PurrLay.Tests;

public sealed class BalancerRecoveryTests
{
    [Theory]
    [InlineData("/admin/handoff")]
    [InlineData("/admin/commitHandoff")]
    public async Task SuccessorCrashBeforeHandoffReplyResumesSavedIdentityAndRooms(string heldPath)
    {
        await using var source = new BalancerProcess();
        await source.WaitReadyAsync();
        var room = "recovery-" + Guid.NewGuid().ToString("N");
        await RegisterRoomAsync(source.Endpoint, room);
        await using var proxy = new ReplyBarrier(source.Endpoint, heldPath);
        await using var successor = new BalancerProcess(proxy.Endpoint);
        await proxy.Held.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var pending = JObject.Parse(File.ReadAllText(successor.StatePath));
        Assert.Equal("initializing", pending.Value<string>("phase"));
        var identity = pending.Value<string>("instanceId");
        Assert.False(string.IsNullOrEmpty(identity));
        Assert.Equal(proxy.Endpoint, pending.Value<string>("pendingPredecessor"));
        Assert.Equal(heldPath == "/admin/commitHandoff", pending.Value<string>("pendingHandoffId") != null);
        var sourceStatus = await JsonAsync(source.Endpoint, "/admin/status", headers: [("balancer_status_local", "1")]);
        Assert.Equal(identity, sourceStatus.Value<string>("pendingSuccessorInstanceId"));
        Assert.Equal(heldPath == "/admin/handoff", sourceStatus.Value<bool>("ready"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusAsync(successor.Endpoint, "/admin/ready"));

        await successor.StopAsync();
        if (heldPath == "/admin/handoff")
            await JsonAsync(source.Endpoint, "/updateConnectionCount", headers: RoomHeaders(room, 9));
        proxy.Release();
        await using var restarted = new BalancerProcess(proxy.Endpoint, successor.Port, successor.StatePath);
        var recovered = await restarted.WaitReadyAsync();
        Assert.Equal(identity, recovered.Value<string>("instanceId"));
        Assert.Equal("authority", JObject.Parse(File.ReadAllText(successor.StatePath)).Value<string>("phase"));
        var listed = (await JsonAsync(restarted.Endpoint, "/list"))["results"]!.Single(item => item.Value<string>("name") == room);
        Assert.Equal(heldPath == "/admin/handoff" ? 9 : 4, listed.Value<int>("connectedPlayers"));
        var snapshot = JObject.Parse(File.ReadAllText(successor.StatePath))["snapshot"]!;
        Assert.Equal("http://127.0.0.1:1", snapshot["roomToServerEndpoint"]![room]!.Value<string>());
        Assert.Equal("room-instance", snapshot["roomInstanceIds"]![room]!.Value<string>());
        Assert.Equal(heldPath == "/admin/handoff" ? 2 : 1, proxy.PrepareCalls);
        Assert.Equal(heldPath == "/admin/commitHandoff" ? 2 : 1, proxy.CommitCalls);
        Assert.Contains((await JsonAsync(source.Endpoint, "/list"))["results"]!, item => item.Value<string>("name") == room);
    }

    [Fact]
    public async Task RetirementResolvesLiveAuthorityPastAnIntermediateForwarder()
    {
        await using var first = new BalancerProcess();
        await first.WaitReadyAsync();
        var room = "chain-" + Guid.NewGuid().ToString("N");
        await RegisterRoomAsync(first.Endpoint, room);
        await using var middle = new BalancerProcess(first.Endpoint);
        await middle.WaitReadyAsync();
        await using var last = new BalancerProcess(middle.Endpoint);
        var authority = await last.WaitReadyAsync();

        var status = await JsonAsync(first.Endpoint, "/admin/status");
        Assert.True(status.Value<bool>("canRetire"));
        Assert.Equal(last.Endpoint, status.Value<string>("successorUrl"));
        var checkpoint = JObject.Parse(File.ReadAllText(first.StatePath));
        Assert.Equal(last.Endpoint, checkpoint.Value<string>("successorUrl"));
        Assert.Equal(authority.Value<string>("instanceId"), checkpoint.Value<string>("successorInstanceId"));
        await middle.StopAsync();
        Assert.Contains((await JsonAsync(first.Endpoint, "/list"))["results"]!, item => item.Value<string>("name") == room);
        Assert.True((await JsonAsync(first.Endpoint, "/admin/status")).Value<bool>("canRetire"));
    }

    private static async Task RegisterRoomAsync(string endpoint, string room)
    {
        await JsonAsync(endpoint, "/registerServer", new JObject
        {
            ["apiEndpoint"] = "http://127.0.0.1:1", ["region"] = "recovery",
            ["instanceId"] = "relay-instance", ["deploymentId"] = "release", ["host"] = "cached-host"
        });
        await JsonAsync(endpoint, "/registerRoom", headers: RoomHeaders(room));
        await JsonAsync(endpoint, "/updateConnectionCount", headers: RoomHeaders(room, 4));
    }

    private static (string, string)[] RoomHeaders(string room, int count = 0) =>
    [
        ("name", room), ("region", "recovery"), ("relay_endpoint", "http://127.0.0.1:1"),
        ("relay_instance_id", "relay-instance"), ("room_instance_id", "room-instance"),
        ("count", count.ToString()), ("count_sequence", count.ToString())
    ];

    private static async Task<JObject> JsonAsync(string endpoint, string path, JObject? body = null,
        (string name, string value)[]? headers = null)
    {
        using var response = await RequestAsync(endpoint, path, body, headers);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return JObject.Parse(text);
    }

    private static async Task<HttpStatusCode> StatusAsync(string endpoint, string path)
    {
        using var response = await RequestAsync(endpoint, path);
        return response.StatusCode;
    }

    private static async Task<HttpResponseMessage> RequestAsync(string endpoint, string path, JObject? body = null,
        (string name, string value)[]? headers = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, endpoint + path);
        request.Headers.Add("internal_key_secret", "PURRNET");
        foreach (var (name, value) in headers ?? []) request.Headers.Add(name, value);
        if (body != null) request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class BalancerProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _output = new();
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("purr-balancer-recovery-");
        private readonly bool _ownsState;
        private volatile bool _listening;
        public int Port { get; }
        public string Endpoint => $"http://127.0.0.1:{Port}";
        public string StatePath { get; }

        public BalancerProcess(string? predecessor = null, int? port = null, string? statePath = null)
        {
            Port = port ?? FreePort();
            StatePath = statePath ?? Path.Combine(_directory.FullName, "state.json");
            _ownsState = statePath == null;
            var build = new DirectoryInfo(AppContext.BaseDirectory);
            var binary = Path.Combine(build.Parent!.Parent!.Parent!.Parent!.FullName,
                "PurrBalancer", "bin", build.Parent.Name, "net8.0", "PurrBalancer.dll");
            Assert.True(File.Exists(binary), $"Missing application build: {binary}");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _directory.FullName,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(binary);
            foreach (var name in new[] { "FLY_APP_NAME", "FLY_PROCESS_GROUP", "FLY_MACHINE_ID", "FLY_PRIVATE_IP",
                         "BALANCER_PREDECESSOR_URL", "BALANCER_SELF_URL", "BALANCER_STATE_PATH", "BALANCER_DEPLOYMENT_ID" })
                start.Environment.Remove(name);
            start.Environment["HOST"] = "127.0.0.1";
            start.Environment["PORT"] = Port.ToString();
            start.Environment["SECRET"] = "PURRNET";
            start.Environment["BALANCER_SELF_URL"] = Endpoint;
            start.Environment["BALANCER_PUBLIC_URL"] = Endpoint;
            start.Environment["BALANCER_STATE_PATH"] = StatePath;
            start.Environment["BALANCER_DEPLOYMENT_ID"] = "recovery";
            if (predecessor != null) start.Environment["BALANCER_PREDECESSOR_URL"] = predecessor;
            _process = new Process { StartInfo = start };
            _process.OutputDataReceived += (_, args) => Capture(args.Data);
            _process.ErrorDataReceived += (_, args) => Capture(args.Data);
            Assert.True(_process.Start());
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        private void Capture(string? line)
        {
            if (line == null) return;
            _output.Enqueue(line);
            if (line == $"Listening on {Endpoint}/") _listening = true;
            while (_output.Count > 100) _output.TryDequeue(out _);
        }

        public async Task<JObject> WaitReadyAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                Assert.False(_process.HasExited, string.Join('\n', _output));
                // .NET 8 HttpListener can accept before its constructor has initialized
                // connection tracking. Probe only after Start has returned in the child.
                if (!_listening)
                {
                    await Task.Delay(40);
                    continue;
                }
                try
                {
                    var status = await JsonAsync(Endpoint, "/admin/status");
                    if (status.Value<bool>("ready")) return status;
                }
                catch (HttpRequestException) { }
                await Task.Delay(40);
            }
            throw new TimeoutException(string.Join('\n', _output));
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
                foreach (var file in _directory.EnumerateFiles("state.json*")) file.Delete();
            if (!_directory.EnumerateFileSystemInfos().Any()) _directory.Delete();
        }
    }

    private sealed class ReplyBarrier : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly ConcurrentBag<Task> _requests = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _accept;
        private readonly string _target;
        private readonly string _heldPath;
        private int _prepareCalls;
        private int _commitCalls;
        public int PrepareCalls => Volatile.Read(ref _prepareCalls);
        public int CommitCalls => Volatile.Read(ref _commitCalls);
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Endpoint { get; } = $"http://127.0.0.1:{FreePort()}";

        public ReplyBarrier(string target, string heldPath)
        {
            _target = target;
            _heldPath = heldPath;
            _listener.Prefixes.Add(Endpoint + "/");
            _listener.Start();
            _accept = AcceptAsync();
        }

        public void Release() => _release.TrySetResult();

        private async Task AcceptAsync()
        {
            try
            {
                while (_listener.IsListening) _requests.Add(ForwardAsync(await _listener.GetContextAsync()));
            }
            catch (HttpListenerException) when (!_listener.IsListening) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ForwardAsync(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url!.AbsolutePath;
                if (path == "/admin/handoff") Interlocked.Increment(ref _prepareCalls);
                if (path == "/admin/commitHandoff") Interlocked.Increment(ref _commitCalls);
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), _target + context.Request.RawUrl);
                request.Headers.Add("internal_key_secret", "PURRNET");
                if (context.Request.HasEntityBody)
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    request.Content = new StringContent(await reader.ReadToEndAsync(), Encoding.UTF8, "application/json");
                }
                using var response = await _client.SendAsync(request);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (path == _heldPath && Held.TrySetResult()) await _release.Task;
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or HttpRequestException or OperationCanceledException) { }
            finally
            {
                try { context.Response.Close(); }
                catch (ObjectDisposedException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Release();
            _listener.Close();
            await _accept;
            await Task.WhenAll(_requests);
            _client.Dispose();
        }
    }
}

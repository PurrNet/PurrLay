using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PurrLay.Tests;

public sealed class WebRtcGatewayRestartTests
{
    [Fact]
    public async Task CrashedGatewayRestartsCancelsOldOffersAndAuthenticatesFreshPeers()
    {
        await using var fixture = new GatewayFixture();
        var runtime = fixture.Start();
        await fixture.WaitHealthyAsync();
        var firstPid = runtime.CurrentProcessId!.Value;
        var first = await fixture.ReadReadyAsync(firstPid);
        Assert.True(PipeRelay.IsClient(first.ConnectionId));
        Assert.NotNull(runtime.PublicUrl);

        fixture.SetControl("hold-offer");
        var oldOffer = runtime.OfferAsync(Offer());
        await WaitUntilAsync(() => fixture.HasControl($"offer-{firstPid}"));
        await KillAsync(firstPid);
        Assert.False(runtime.Available);
        Assert.Null(runtime.PublicUrl);
        var unavailable = await HTTPRestAPI.OnRequest(Offer());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.status);
        Assert.NotEqual(HttpStatusCode.OK, (await oldOffer.WaitAsync(TimeSpan.FromSeconds(3))).status);
        fixture.RemoveControl("hold-offer");

        await fixture.WaitHealthyAsync(firstPid);
        var secondPid = runtime.CurrentProcessId!.Value;
        var second = await fixture.ReadReadyAsync(secondPid);
        await WaitUntilAsync(() => !PipeRelay.IsClient(first.ConnectionId));
        Assert.NotEqual(first.ConnectionId, second.ConnectionId);
        Assert.NotEqual(first.TokenHash, second.TokenHash);
        Assert.True(PipeRelay.IsClient(second.ConnectionId));
        Assert.Equal(HttpStatusCode.OK, (await runtime.OfferAsync(Offer())).status);

        await KillAsync(secondPid);
        await fixture.WaitHealthyAsync(secondPid);
        await WaitUntilAsync(() => !PipeRelay.IsClient(second.ConnectionId));
        Assert.Equal(3, fixture.Starts().Length);
        Assert.Equal(HttpStatusCode.OK, (await runtime.OfferAsync(Offer())).status);
    }

    [Theory]
    [InlineData("unhealthy")]
    [InlineData("hang-health")]
    public async Task UnhealthyOrUnresponsiveGatewayIsKilledAndReplaced(string failure)
    {
        await using var fixture = new GatewayFixture();
        var runtime = fixture.Start();
        await fixture.WaitHealthyAsync();
        var oldPid = runtime.CurrentProcessId!.Value;
        fixture.SetControl($"{failure}-{oldPid}");
        await fixture.WaitHealthyAsync(oldPid);
        Assert.False(IsRunning(oldPid));
        Assert.Equal(HttpStatusCode.OK, (await runtime.OfferAsync(Offer())).status);
    }

    [Fact]
    public async Task FailedStartsBackOffAndRecoverWithoutRestartingRelay()
    {
        await using var fixture = new GatewayFixture();
        fixture.SetControl("fail-start");
        var runtime = fixture.Start();
        await WaitUntilAsync(() => fixture.Starts().Length >= 5);
        Assert.False(runtime.Available);
        Assert.Null(runtime.PublicUrl);
        var starts = fixture.Starts();
        for (var i = 1; i < 5; i++)
        {
            var expected = Math.Min(200 * Math.Pow(2, i - 1), 600);
            Assert.True((starts[i].At - starts[i - 1].At).TotalMilliseconds >= expected - 30,
                $"Restart {i} did not respect its retry delay.");
        }

        fixture.RemoveControl("fail-start");
        await fixture.WaitHealthyAsync();
        Assert.Equal(HttpStatusCode.OK, (await runtime.OfferAsync(Offer())).status);
    }

    [Fact]
    public async Task OccupiedUdpPortCanRecoverAfterItIsReleased()
    {
        await using var fixture = new GatewayFixture();
        using var occupied = new UdpClient(new IPEndPoint(IPAddress.Loopback, fixture.UdpPort));
        var runtime = fixture.Start();
        await WaitUntilAsync(() => fixture.Starts().Length >= 2);
        Assert.False(runtime.Available);
        occupied.Dispose();
        await fixture.WaitHealthyAsync();
        Assert.Equal(HttpStatusCode.OK, (await runtime.OfferAsync(Offer())).status);
    }

    [Fact]
    public async Task ShutdownDuringStartupKillsChildAndReleasesPorts()
    {
        await using var fixture = new GatewayFixture();
        fixture.SetControl("hang-health");
        var runtime = fixture.Start();
        await WaitUntilAsync(() => runtime.CurrentProcessId.HasValue &&
            fixture.HasControl($"health-{runtime.CurrentProcessId.Value}"));
        var pid = runtime.CurrentProcessId!.Value;
        runtime.Dispose();
        runtime.Dispose();
        await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(IsRunning(pid));
        Assert.False(runtime.Available);
        Assert.Null(HTTPRestAPI.webRtcServer);
        fixture.AssertPortsReleased();
        var starts = fixture.Starts().Length;
        await Task.Delay(800);
        Assert.Equal(starts, fixture.Starts().Length);
    }

    [Fact]
    public async Task ShutdownDuringRetryDelayPreventsLateRestart()
    {
        await using var fixture = new GatewayFixture();
        fixture.SetControl("fail-start");
        var runtime = fixture.Start();
        await WaitUntilAsync(() => fixture.Starts().Length >= 2 && !runtime.CurrentProcessId.HasValue);
        runtime.Dispose();
        await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var starts = fixture.Starts().Length;
        await Task.Delay(800);
        Assert.Equal(starts, fixture.Starts().Length);
        Assert.Null(runtime.CurrentProcessId);
        fixture.AssertPortsReleased();
    }

    static TestHttpRequest Offer() => new("/webrtc/offer")
    {
        Method = WatsonWebserver.Core.HttpMethod.POST,
        Data = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"offer\",\"sdp\":\"v=0\"}"))
    };

    static async Task KillAsync(int pid)
    {
        using var child = Process.GetProcessById(pid);
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    sealed class GatewayFixture : IAsyncDisposable
    {
        readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("purrlay-gateway-tests-");
        readonly WebRtcGatewayRuntime? _originalRuntime = HTTPRestAPI.webRtcRuntime;
        readonly IUdpServer? _originalBridge = HTTPRestAPI.webRtcServer;
        readonly string _executable = Path.ChangeExtension(typeof(GatewayProbe).Assembly.Location,
            OperatingSystem.IsWindows() ? ".exe" : null);
        WebRtcGatewayRuntime? _runtime;
        readonly int _httpPort = ReserveTcpPort();
        readonly int _bridgePort = ReserveTcpPort();
        public int UdpPort { get; } = ReserveUdpPort();

        public WebRtcGatewayRuntime Start()
        {
            Assert.True(File.Exists(_executable), $"Missing test apphost: {_executable}");
            _runtime = WebRtcGatewayRuntime.Start(new WebRtcGatewayOptions
            {
                ExecutablePath = _executable,
                BridgeEndpoint = new IPEndPoint(IPAddress.Loopback, _bridgePort),
                HttpEndpoint = new IPEndPoint(IPAddress.Loopback, _httpPort),
                Environment = new Dictionary<string, string>
                {
                    ["PURR_GATEWAY_TEST_DIRECTORY"] = _directory.FullName,
                    ["WEBRTC_UDP_PORT"] = UdpPort.ToString(),
                    ["WEBRTC_BIND_ADDRESS"] = "127.0.0.1"
                },
                StartupTimeout = TimeSpan.FromSeconds(3),
                HealthInterval = TimeSpan.FromMilliseconds(100),
                HealthTimeout = TimeSpan.FromMilliseconds(150),
                InitialRestartDelay = TimeSpan.FromMilliseconds(200),
                MaxRestartDelay = TimeSpan.FromMilliseconds(600),
                StableWindow = TimeSpan.FromSeconds(2)
            });
            HTTPRestAPI.webRtcRuntime = _runtime;
            return _runtime;
        }

        public Task WaitHealthyAsync(int? previousPid = null) => WaitUntilAsync(() =>
            _runtime!.Available && _runtime.CurrentProcessId != previousPid);

        public async Task<GatewayProbe.ProbeReady> ReadReadyAsync(int pid)
        {
            var path = Path.Combine(_directory.FullName, $"ready-{pid}");
            await WaitUntilAsync(() => File.Exists(path));
            return JsonSerializer.Deserialize<GatewayProbe.ProbeReady>(await File.ReadAllTextAsync(path))!;
        }

        public void SetControl(string name) => File.WriteAllText(Path.Combine(_directory.FullName, name), "1");
        public void RemoveControl(string name) => File.Delete(Path.Combine(_directory.FullName, name));
        public bool HasControl(string name) => File.Exists(Path.Combine(_directory.FullName, name));

        public (int Pid, DateTime At)[] Starts()
        {
            var path = Path.Combine(_directory.FullName, "starts");
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            return lines.Take(lines.Length - 1).Select(line =>
            {
                var parts = line.Split(',');
                return (int.Parse(parts[0]), new DateTime(long.Parse(parts[1]), DateTimeKind.Utc));
            }).ToArray();
        }

        public void AssertPortsReleased()
        {
            using var http = new TcpListener(IPAddress.Loopback, _httpPort);
            using var bridge = new TcpListener(IPAddress.Loopback, _bridgePort);
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, UdpPort));
            http.Start();
            bridge.Start();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_runtime != null)
                {
                    _runtime.Dispose();
                    await _runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            finally
            {
                foreach (var (pid, _) in Starts())
                {
                    try
                    {
                        using var child = Process.GetProcessById(pid);
                        if (!child.HasExited && string.Equals(child.MainModule?.FileName, _executable,
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                            await KillAsync(pid);
                    }
                    catch (ArgumentException) { }
                }
                HTTPRestAPI.webRtcRuntime = _originalRuntime;
                HTTPRestAPI.webRtcServer = _originalBridge;
                var tempRoot = Path.GetFullPath(Path.GetTempPath());
                if (_directory.FullName.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                    _directory.Name.StartsWith("purrlay-gateway-tests-", StringComparison.Ordinal))
                    _directory.Delete(recursive: true);
            }
        }

        static int ReserveTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        static int ReserveUdpPort()
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        }
    }
}

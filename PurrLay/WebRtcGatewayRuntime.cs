using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PurrBalancer;
using WatsonWebserver.Core;

namespace PurrLay;

internal sealed record WebRtcGatewayOptions
{
    internal required string ExecutablePath { get; init; }
    internal IPEndPoint BridgeEndpoint { get; init; } = new(IPAddress.Loopback, 8091);
    internal IPEndPoint HttpEndpoint { get; init; } = new(IPAddress.Loopback, 8090);
    internal IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    internal int MaxSessions { get; init; } = 1024;
    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan InitialRestartDelay { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan MaxRestartDelay { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan StableWindow { get; init; } = TimeSpan.FromSeconds(60);
}

internal sealed class WebRtcGatewayRuntime : IDisposable
{
    readonly WebRtcGatewayOptions _options;
    readonly CancellationTokenSource _stop = new();
    readonly object _gate = new();
    Attempt? _current;
    int _disposed;

    WebRtcGatewayRuntime(WebRtcGatewayOptions options)
    {
        _options = options;
        Completion = Task.Run(SuperviseAsync);
    }

    internal Task Completion { get; }
    internal int? CurrentProcessId
    {
        get
        {
            lock (_gate) return _current?.ProcessId;
        }
    }

    internal bool Available => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _current) is { Available: true };
    internal string? PublicUrl => Available ? Program.GetRelayEndpoint().TrimEnd('/') + "/webrtc/offer" : null;

    internal static Task<WebRtcGatewayRuntime?> StartAsync()
    {
        if (!bool.TryParse(Env.TryGetValueOrDefault("WEBRTC_ENABLED", "false"), out var enabled) || !enabled)
            return Task.FromResult<WebRtcGatewayRuntime?>(null);
        var path = Env.TryGetValueOrDefault("WEBRTC_GATEWAY_PATH", Path.Combine(AppContext.BaseDirectory,
            "PurrLay.WebRtcGateway" + (OperatingSystem.IsWindows() ? ".exe" : "")));
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("WebRTC disabled: gateway executable is missing. WebSockets remain available.");
            return Task.FromResult<WebRtcGatewayRuntime?>(null);
        }
        var onFly = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLY_APP_NAME")) ||
                    !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLY_PROCESS_GROUP"));
        if (onFly && string.IsNullOrWhiteSpace(Env.TryGetValueOrDefault("WEBRTC_PUBLIC_IP", "")))
        {
            Console.Error.WriteLine("WebRTC disabled: set WEBRTC_PUBLIC_IP to this Fly app's dedicated public IPv4 address.");
            return Task.FromResult<WebRtcGatewayRuntime?>(null);
        }

        try
        {
            var environment = new Dictionary<string, string>();
            foreach (var name in new[] { "WEBRTC_BIND_ADDRESS", "WEBRTC_PUBLIC_IP", "WEBRTC_MAX_SESSIONS" })
                if (Env.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                    environment[name] = value;
            environment["WEBRTC_UDP_PORT"] = Env.TryGetValueOrDefault("WEBRTC_UDP_PORT", "7779");
            if (onFly && !environment.ContainsKey("WEBRTC_BIND_ADDRESS"))
                environment["WEBRTC_BIND_ADDRESS"] = "fly-global-services";
            return Task.FromResult<WebRtcGatewayRuntime?>(Start(new WebRtcGatewayOptions
            {
                ExecutablePath = Path.GetFullPath(path),
                BridgeEndpoint = ParseLoopbackEndpoint(Env.TryGetValueOrDefault("PURR_WEBRTC_BRIDGE_ADDRESS", "127.0.0.1:8091")),
                HttpEndpoint = ParseLoopbackEndpoint(Env.TryGetValueOrDefault("WEBRTC_HTTP_ADDRESS", "127.0.0.1:8090")),
                MaxSessions = Env.TryGetIntOrDefault("WEBRTC_MAX_SESSIONS", 1024),
                Environment = environment
            }));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"WebRTC disabled: {exception.Message}. WebSockets remain available.");
            return Task.FromResult<WebRtcGatewayRuntime?>(null);
        }
    }

    internal static WebRtcGatewayRuntime Start(WebRtcGatewayOptions options)
    {
        ParseLoopbackEndpoint(options.BridgeEndpoint.ToString());
        ParseLoopbackEndpoint(options.HttpEndpoint.ToString());
        if (options.MaxSessions is < 1 or > 65535 || options.InitialRestartDelay > options.MaxRestartDelay ||
            new[] { options.StartupTimeout, options.HealthInterval, options.HealthTimeout, options.InitialRestartDelay,
                    options.MaxRestartDelay, options.StableWindow }.Any(value => value <= TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(options));
        return new WebRtcGatewayRuntime(options);
    }

    internal static IPEndPoint ParseLoopbackEndpoint(string value)
    {
        if (!IPEndPoint.TryParse(value, out var endpoint) || !IPAddress.IsLoopback(endpoint.Address) || endpoint.Port == 0)
            throw new ArgumentException("WebRTC HTTP and bridge addresses must be loopback IP addresses with a nonzero port.");
        return endpoint;
    }

    async Task SuperviseAsync()
    {
        var delay = _options.InitialRestartDelay;
        while (!_stop.IsCancellationRequested)
        {
            var attempt = new Attempt(_options, _stop.Token);
            var healthySince = 0L;
            var wasStable = false;
            try
            {
                lock (_gate)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    if (_disposed != 0) break;
                    Volatile.Write(ref _current, attempt);
                    attempt.Start();
                }
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    startup.CancelAfter(_options.StartupTimeout);
                    while (!await attempt.CheckHealthAsync(startup.Token))
                    {
                        if (attempt.Exited.IsCompleted)
                            throw new IOException("WebRTC gateway exited during startup.");
                        await Task.Delay(TimeSpan.FromMilliseconds(100), startup.Token);
                    }
                }
                lock (_gate)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    if (!attempt.IsRunning) throw new IOException("WebRTC gateway exited during startup.");
                    attempt.MarkHealthy();
                }
                healthySince = Stopwatch.GetTimestamp();
                Console.WriteLine("WebRTC gateway ready; browser connections can use WebRTC with WebSocket fallback.");
                while (true)
                {
                    using var interval = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    var tick = Task.Delay(_options.HealthInterval, interval.Token);
                    if (await Task.WhenAny(attempt.Exited, tick) == attempt.Exited)
                    {
                        interval.Cancel();
                        throw new IOException("WebRTC gateway exited.");
                    }
                    await tick;
                    if (!await attempt.CheckHealthAsync(_stop.Token))
                        throw new IOException("WebRTC gateway health check failed.");
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (!_stop.IsCancellationRequested)
                    Console.Error.WriteLine($"WebRTC gateway unavailable: {exception.Message}. WebSockets remain available.");
            }
            finally
            {
                wasStable = healthySince != 0 && Stopwatch.GetElapsedTime(healthySince) >= _options.StableWindow;
                lock (_gate)
                {
                    if (ReferenceEquals(_current, attempt)) Volatile.Write(ref _current, null);
                    Interlocked.CompareExchange(ref HTTPRestAPI.webRtcServer, null, attempt.Bridge);
                    attempt.Stop();
                }
                await attempt.DisposeAsync();
            }
            if (_stop.IsCancellationRequested) break;
            if (wasStable) delay = _options.InitialRestartDelay;
            Console.Error.WriteLine($"Restarting WebRTC gateway in {delay.TotalSeconds:0.###} seconds.");
            try { await Task.Delay(delay, _stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRestartDelay.Ticks));
        }
    }

    internal async Task<ApiResponse> OfferAsync(HttpRequestBase request)
    {
        var attempt = Volatile.Read(ref _current);
        if (Volatile.Read(ref _disposed) != 0 || attempt is not { Available: true })
            return ApiResponse.FromError("WebRTC gateway is unavailable.", HttpStatusCode.ServiceUnavailable);
        var response = await ProxyOfferAsync(request, attempt.Http, new Uri(attempt.Gateway, "offer"), attempt.Stopping);
        return Available && ReferenceEquals(attempt, Volatile.Read(ref _current)) && !attempt.Stopping.IsCancellationRequested
            ? response
            : ApiResponse.FromError("WebRTC gateway is unavailable.", HttpStatusCode.ServiceUnavailable);
    }

    internal static async Task<ApiResponse> ProxyOfferAsync(HttpRequestBase request, HttpClient http, Uri target,
        CancellationToken stopping = default)
    {
        const int bodyLimit = 128 * 1024;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            byte[] body;
            try { body = await ReadLimitedAsync(request.Data, bodyLimit, timeout.Token); }
            catch (InvalidDataException) { return ApiResponse.FromError("WebRTC offer is too large.", HttpStatusCode.RequestEntityTooLarge); }
            JObject offer;
            try { offer = JObject.Parse(Encoding.UTF8.GetString(body)); }
            catch (JsonException) { return ApiResponse.FromError("Invalid WebRTC offer JSON.", HttpStatusCode.BadRequest); }
            if (offer["type"]?.Type != JTokenType.String || (string?)offer["type"] != "offer" || offer["sdp"]?.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace((string?)offer["sdp"]))
                return ApiResponse.FromError("Expected an SDP offer.", HttpStatusCode.BadRequest);
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var message = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, target) { Content = content };
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var answer = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), bodyLimit, timeout.Token);
            JObject answerJson;
            try { answerJson = JObject.Parse(Encoding.UTF8.GetString(answer)); }
            catch (JsonException) { return ApiResponse.FromError("Invalid response from WebRTC gateway.", HttpStatusCode.BadGateway); }
            if (response.IsSuccessStatusCode && (answerJson["type"]?.Type != JTokenType.String || (string?)answerJson["type"] != "answer" ||
                answerJson["sdp"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)answerJson["sdp"])))
                return ApiResponse.FromError("Invalid answer from WebRTC gateway.", HttpStatusCode.BadGateway);
            return new ApiResponse(answer, response.StatusCode, ContentType.JSON);
        }
        catch (OperationCanceledException) { return ApiResponse.FromError("WebRTC negotiation timed out.", HttpStatusCode.GatewayTimeout); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or ObjectDisposedException)
        { return ApiResponse.FromError("WebRTC gateway is unavailable.", HttpStatusCode.BadGateway); }
    }

    internal static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)result.Length)), token);
            if (read == 0) return result.ToArray();
            result.Write(buffer, 0, read);
            if (result.Length > limit) throw new InvalidDataException("WebRTC signaling body limit exceeded.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _current?.Stop();
        }
    }

    sealed class Attempt(WebRtcGatewayOptions options, CancellationToken lifetime) : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Process? _process;
        bool _started;
        int _healthy;

        internal WebRtcServer? Bridge { get; private set; }
        internal HttpClient Http { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };
        internal Uri Gateway { get; } = new($"http://{options.HttpEndpoint}/");
        internal CancellationToken Stopping { get; private set; }
        internal Task Exited { get; private set; } = Task.CompletedTask;
        internal bool IsRunning
        {
            get
            {
                try { return _started && _process is { HasExited: false } && Bridge is { IsRunning: true }; }
                catch (InvalidOperationException) { return false; }
            }
        }
        internal int? ProcessId
        {
            get
            {
                try { return _started && _process is { HasExited: false } ? _process.Id : null; }
                catch (InvalidOperationException) { return null; }
            }
        }
        internal bool Available => Volatile.Read(ref _healthy) == 1 && IsRunning;

        internal void Start()
        {
            Stopping = _stop.Token;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            Bridge = new WebRtcServer(options.BridgeEndpoint, token, HTTPRestAPI.CreateCallbacks(3), options.MaxSessions);
            HTTPRestAPI.webRtcServer = Bridge;
            var start = new ProcessStartInfo(Path.GetFullPath(options.ExecutablePath))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            foreach (var (key, value) in options.Environment) start.Environment[key] = value;
            start.Environment["PURR_WEBRTC_BRIDGE_TOKEN"] = token;
            start.Environment["PURR_WEBRTC_PARENT_PIPE"] = "true";
            start.Environment["PURR_WEBRTC_BRIDGE_ADDRESS"] = Bridge.Endpoint.ToString();
            start.Environment["WEBRTC_HTTP_ADDRESS"] = options.HttpEndpoint.ToString();
            start.Environment["WEBRTC_MAX_SESSIONS"] = options.MaxSessions.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _process = new Process { StartInfo = start };
            if (!_process.Start()) throw new IOException("Could not start the WebRTC gateway.");
            _started = true;
            Exited = _process.WaitForExitAsync();
        }

        internal void MarkHealthy() => Volatile.Write(ref _healthy, 1);

        internal async Task<bool> CheckHealthAsync(CancellationToken cancellation)
        {
            if (!IsRunning) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellation);
            timeout.CancelAfter(options.HealthTimeout);
            try
            {
                var request = Http.GetAsync(new Uri(Gateway, "health"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (await Task.WhenAny(request, Exited) == Exited) timeout.Cancel();
                using var response = await request;
                return response.IsSuccessStatusCode && IsRunning && !timeout.IsCancellationRequested;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException)
            { return false; }
        }

        internal void Stop()
        {
            Volatile.Write(ref _healthy, 0);
            _stop.Cancel();
            Bridge?.Dispose();
            Kill();
        }

        void Kill()
        {
            try { if (_started && _process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception exception)
            { Console.Error.WriteLine($"Could not stop WebRTC gateway: {exception.Message}"); }
        }

        async Task ReapAsync()
        {
            if (!_started) return;
            // Never reuse the gateway ports while an older child could still own them.
            while (await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(5))) != Exited)
                Kill();
            await Exited;
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            await Task.WhenAll(ReapAsync(), Bridge?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            _process?.Dispose();
            Http.Dispose();
            _stop.Dispose();
        }
    }
}

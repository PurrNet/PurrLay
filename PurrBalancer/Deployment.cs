using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using WatsonWebserver.Core;

namespace PurrBalancer;

public static partial class HTTPRestAPI
{
    internal static readonly object StateGate = new();
    private static readonly HttpClient DeploymentClient = new() { Timeout = TimeSpan.FromSeconds(25) };
    private static readonly HashSet<string> _drainingRelayEndpoints = new(StringComparer.Ordinal);
    private static readonly List<LegacyDependency> _legacyDependencies = [];
    private static string _instanceId = Guid.NewGuid().ToString("N");
    private static string? _selfUrl;
    private static string? _successorUrl;
    private static string? _successorInstanceId;
    private static string? _handoffId;
    private static string? _handoffTarget;
    private static string? _handoffInstance;
    private static JObject? _committedSnapshot;
    private static bool _ready = true;
    private static string? _startupError;
    private static bool _initializing;
    private static string? _pendingPredecessor;
    private static string? _pendingHandoffId;
    private static TaskCompletionSource<bool> _readiness = CompletedReadiness();

    internal static bool IsAuthority => _ready && _successorUrl == null;

    private sealed class LegacyDependency
    {
        public string url = "";
        public RelayServer[] relayServers = [];
    }

    private static TaskCompletionSource<bool> CompletedReadiness()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(true);
        return completion;
    }

    internal static void ConfigureDeployment()
    {
        var app = Env.TryGetValueOrDefault("FLY_APP_NAME", "");
        var machine = Env.TryGetValueOrDefault("FLY_MACHINE_ID", "");
        var defaultUrl = string.IsNullOrEmpty(app) || string.IsNullOrEmpty(machine)
            ? $"http://localhost:{Env.TryGetIntOrDefault("PORT", 8080)}"
            : $"http://{machine}.vm.{app}.internal:{Env.TryGetIntOrDefault("PORT", 8080)}";
        _selfUrl = NormalizeUrl(Env.TryGetValueOrDefault("BALANCER_SELF_URL", defaultUrl));
        if (Env.TryGetValue("BALANCER_PREDECESSOR_URL", out var predecessor) && !string.IsNullOrWhiteSpace(predecessor))
        {
            _ready = false;
            _initializing = true;
            _pendingPredecessor = NormalizeUrl(predecessor);
            _readiness = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        LoadCheckpoint();
    }

    internal static async Task InitializeDeploymentAsync()
    {
        if (_ready || _checkpointFailed || _checkpointLoaded)
            return;
        for (var attempt = 0; !_checkpointFailed; ++attempt)
        try
        {
            var predecessor = _pendingPredecessor ?? NormalizeUrl(Env.TryGetValueOrDefault("BALANCER_PREDECESSOR_URL", ""));
            for (var redirects = 0; ; ++redirects)
            {
                if (redirects >= 8 || predecessor == _selfUrl)
                    throw new InvalidOperationException("Invalid balancer predecessor chain.");
                if (_pendingHandoffId != null)
                {
                    await CompleteHandoffAsync(predecessor);
                    break;
                }
                var status = await RequestJsonAsync(predecessor, "/admin/status");
                if (status.status == HttpStatusCode.NotFound)
                {
                    await BootstrapLegacyAsync(predecessor);
                    break;
                }
                RequireSuccess(status);
                var current = ParseObject(status);
                if (current.Value<int?>("handoffVersion") != 1)
                    throw new InvalidOperationException("Predecessor does not support a recognized handoff protocol.");
                var successor = current.Value<string>("successorUrl");
                if (!string.IsNullOrEmpty(successor))
                {
                    predecessor = NormalizeUrl(successor);
                    lock (StateGate)
                    {
                        _pendingPredecessor = predecessor;
                        PersistCheckpoint();
                    }
                    continue;
                }
                if (current.Value<bool?>("ready") != true)
                    throw new HttpRequestException("Predecessor is not ready for handoff.", null, HttpStatusCode.ServiceUnavailable);
                await CompleteHandoffAsync(predecessor);
                break;
            }
            lock (StateGate)
            {
                _ready = true;
                _initializing = false;
                _pendingPredecessor = null;
                _pendingHandoffId = null;
                _startupError = null;
                PersistCheckpoint();
                _readiness.TrySetResult(true);
            }
            return;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            _startupError = $"Retrying balancer handoff: {error.Message}";
            await Task.Delay((int)Math.Min(5000L, 500L + (long)attempt * 250));
        }
        catch (Exception error)
        {
            _startupError = error.Message;
            Console.Error.WriteLine($"Balancer handoff failed; readiness remains disabled: {error.Message}");
            return;
        }
    }

    private static async Task CompleteHandoffAsync(string predecessor)
    {
        var request = new JObject { ["successorUrl"] = _selfUrl, ["successorInstanceId"] = _instanceId };
        if (_pendingHandoffId == null)
        {
            var prepared = await RequestJsonAsync(predecessor, "/admin/handoff", request);
            RequireSuccess(prepared);
            var preparedBody = ParseObject(prepared);
            ValidateSnapshot((JObject)preparedBody["snapshot"]!);
            lock (StateGate)
            {
                _pendingHandoffId = preparedBody.Value<string>("handoffId")
                    ?? throw new InvalidOperationException("Predecessor did not issue a handoff ID.");
                PersistCheckpoint();
            }
        }
        request["handoffId"] = _pendingHandoffId;
        // Persisted identity/token make a lost commit response resumable after a crash.
        ApiResponse committed;
        for (var attempt = 0; ; ++attempt)
        {
            try
            {
                committed = await RequestJsonAsync(predecessor, "/admin/commitHandoff", request);
                RequireSuccess(committed);
                break;
            }
            catch (Exception error)
            {
                _startupError = $"Retrying committed handoff: {error.Message}";
                await Task.Delay((int)Math.Min(5000L, 500L + (long)attempt * 250));
            }
        }
        lock (StateGate)
            RestoreSnapshot((JObject)ParseObject(committed)["snapshot"]!);
    }

    public static async Task<ApiResponse> OnRequest(HttpRequestBase req)
    {
        if (req.Url == null)
            return ApiResponse.FromError("Invalid URL", HttpStatusCode.BadRequest);
        var path = req.Url.RawWithoutQuery;
        if (path.StartsWith("/admin/", StringComparison.Ordinal))
            return await HandleAdminAsync(req, path);
        if (req.Method == WatsonWebserver.Core.HttpMethod.OPTIONS)
            return new ApiResponse(HttpStatusCode.NoContent);
        var successor = GetSuccessor();
        if (successor != null)
            return await ForwardAsync(successor, req);
        if (!_ready)
        {
            try { await _readiness.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
            catch (TimeoutException) { return ApiResponse.FromError("Balancer is waiting for state handoff.", HttpStatusCode.ServiceUnavailable); }
        }
        if (IsRegistryMutation(path) && !IsAuthenticated(req))
            return new ApiResponse(HttpStatusCode.Unauthorized);
        if (path.StartsWith("/relay/", StringComparison.Ordinal))
            return await RelayProxyAsync(req, path);

        var legacyResponse = await HandleLegacyAsync(req, path);
        if (legacyResponse.HasValue)
            return legacyResponse.Value;

        Task<ApiResponse>? pending = null;
        lock (StateGate)
        {
            successor = _successorUrl;
            if (successor == null)
            {
                if (!IsAuthority)
                    return ApiResponse.FromError("Balancer is not ready to accept mutations.", HttpStatusCode.ServiceUnavailable);
                pending = OnLocalRequest(req);
                if (IsRegistryMutation(path) && pending.IsCompletedSuccessfully && (int)pending.Result.status is >= 200 and < 300)
                    PersistCheckpoint();
            }
        }
        return pending != null ? await pending : await ForwardAsync(successor!, req);
    }

    private static string? GetSuccessor()
    {
        lock (StateGate)
            return _successorUrl;
    }

    private static bool IsAuthenticated(HttpRequestBase req) =>
        string.Equals(req.RetrieveHeaderValue("internal_key_secret"), Program.SECRET_INTERNAL, StringComparison.Ordinal);

    private static bool IsRegistryMutation(string path) => path is
        "/registerServer" or "/unregisterServer" or "/registerRoom" or "/unregisterRoom" or "/updateConnectionCount";

    private static async Task<ApiResponse> HandleAdminAsync(HttpRequestBase req, string path)
    {
        if (path == "/admin/ready")
        {
            lock (StateGate)
                return new ApiResponse(IsAuthority || _successorUrl != null ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        }
        if (!IsAuthenticated(req))
            return new ApiResponse(HttpStatusCode.Unauthorized);
        if (path == "/admin/status")
        {
            JObject status;
            lock (StateGate)
            {
                status = new JObject
                {
                    ["handoffVersion"] = 1, ["instanceId"] = _instanceId,
                    ["deploymentId"] = Env.TryGetValueOrDefault("BALANCER_DEPLOYMENT_ID", ""),
                    ["selfUrl"] = _selfUrl, ["ready"] = IsAuthority, ["successorUrl"] = _successorUrl,
                    ["successorInstanceId"] = _successorInstanceId,
                    ["pendingSuccessorUrl"] = _handoffTarget, ["pendingSuccessorInstanceId"] = _handoffInstance,
                    ["phase"] = _initializing ? "initializing" : _successorUrl != null ? "forwarder" : "authority",
                    ["startupError"] = _startupError, ["canRetire"] = false,
                    ["legacyDependencies"] = new JArray(_legacyDependencies.Select(dependency => new JObject
                    {
                        ["url"] = dependency.url,
                        ["relayEndpoints"] = JArray.FromObject(dependency.relayServers.Select(server => server.apiEndpoint))
                    })),
                    ["relayServers"] = JArray.FromObject(_relayServers)
                };
            }
            var next = status.Value<string>("successorUrl");
            if (next != null && req.RetrieveHeaderValue("balancer_status_local") != "1")
            {
                try
                {
                    var visited = new HashSet<string>(StringComparer.Ordinal) { _selfUrl ?? "" };
                    var expectedInstance = _successorInstanceId;
                    for (var hop = 0; hop < 8 && visited.Add(next); ++hop)
                    {
                        var response = await RequestJsonAsync(next, "/admin/status", localStatus: true);
                        if (response.status != HttpStatusCode.OK) break;
                        var successorStatus = ParseObject(response);
                        if (successorStatus.Value<string>("instanceId") != expectedInstance) break;
                        var later = successorStatus.Value<string>("successorUrl");
                        if (!string.IsNullOrEmpty(later))
                        {
                            expectedInstance = successorStatus.Value<string>("successorInstanceId");
                            if (string.IsNullOrEmpty(expectedInstance)) break;
                            next = NormalizeUrl(later);
                            continue;
                        }
                        if (successorStatus.Value<bool?>("ready") == true &&
                            !(successorStatus["legacyDependencies"]?.Any(item => item.Value<string>("url") == _selfUrl) ?? true))
                        {
                            lock (StateGate)
                            {
                                _successorUrl = next;
                                _successorInstanceId = successorStatus.Value<string>("instanceId");
                                PersistCheckpoint();
                                status["successorUrl"] = next;
                                status["canRetire"] = true;
                            }
                        }
                        break;
                    }
                }
                catch { /* An unreachable successor is not proof that this process can be retired. */ }
            }
            return new ApiResponse(status);
        }
        if (req.Method != WatsonWebserver.Core.HttpMethod.POST)
            return new ApiResponse(HttpStatusCode.MethodNotAllowed);
        var body = JObject.Parse(req.DataAsString);
        lock (StateGate)
        {
            if (path is "/admin/handoff" or "/admin/commitHandoff")
                return HandoffUnderLock(path, body);
            if (path == "/admin/activateRelay")
            {
                if (!IsAuthority)
                    return ApiResponse.FromError("Balancer is not the active authority.", HttpStatusCode.Conflict);
                var endpoint = body.Value<string>("apiEndpoint");
                var instance = body.Value<string>("instanceId");
                lock (_relayServers)
                {
                    var target = _relayServers.FindIndex(server => server.apiEndpoint == endpoint && server.instanceId == instance);
                    if (target < 0 || string.IsNullOrEmpty(instance))
                        return ApiResponse.FromError("Relay registration changed or is missing.", HttpStatusCode.Conflict);
                    var region = _relayServers[target].region;
                    _activatedRegions.Add(region);
                    _activeRelayDeployments[region] = new ActiveRelayDeployment
                    {
                        apiEndpoint = _relayServers[target].apiEndpoint,
                        deploymentId = _relayServers[target].deploymentId
                    };
                    for (var i = 0; i < _relayServers.Count; ++i)
                    {
                        var server = _relayServers[i];
                        if (server.region != region) continue;
                        server.draining = i != target;
                        if (server.draining) _drainingRelayEndpoints.Add(server.apiEndpoint);
                        else _drainingRelayEndpoints.Remove(server.apiEndpoint);
                        _relayServers[i] = server;
                    }
                }
                PersistCheckpoint();
                return RoomMutationAccepted();
            }
        }
        return new ApiResponse(HttpStatusCode.NotFound);
    }

    private static ApiResponse HandoffUnderLock(string path, JObject body)
    {
        var target = NormalizeUrl(body.Value<string>("successorUrl") ?? "");
        var instance = body.Value<string>("successorInstanceId");
        if (target == _selfUrl || string.IsNullOrEmpty(instance))
            return ApiResponse.FromError("Invalid successor.", HttpStatusCode.BadRequest);
        if (_handoffId != null && (_handoffTarget != target || _handoffInstance != instance))
            return ApiResponse.FromError("A different successor already owns this handoff.", HttpStatusCode.Conflict);
        if (!_ready && _committedSnapshot == null)
            return ApiResponse.FromError("Balancer is not ready for handoff.", HttpStatusCode.ServiceUnavailable);
        if (path == "/admin/handoff")
        {
            _handoffId ??= Guid.NewGuid().ToString("N");
            _handoffTarget = target;
            _handoffInstance = instance;
            PersistCheckpoint();
            return new ApiResponse(new JObject
            {
                ["handoffId"] = _handoffId, ["snapshot"] = _committedSnapshot?.DeepClone() ?? CaptureSnapshot()
            });
        }
        if (_handoffId == null || body.Value<string>("handoffId") != _handoffId)
            return ApiResponse.FromError("Unknown handoff.", HttpStatusCode.Conflict);
        if (_committedSnapshot == null)
        {
            _committedSnapshot = CaptureSnapshot();
            _successorUrl = target;
            _successorInstanceId = instance;
            _ready = false;
            try { PersistCheckpoint(); }
            catch
            {
                _successorUrl = null;
                _successorInstanceId = null;
                _committedSnapshot = null;
                throw;
            }
        }
        return new ApiResponse(new JObject { ["snapshot"] = _committedSnapshot.DeepClone() });
    }

    private static JObject CaptureSnapshot()
    {
        lock (_relayServers)
        lock (_roomsLock)
            return new JObject
            {
                ["version"] = 1,
                ["relayServers"] = JArray.FromObject(_relayServers),
                ["lastRelayInstanceIds"] = JObject.FromObject(_lastRelayInstanceIds),
                ["retiredRelayInstanceIds"] = JObject.FromObject(_retiredRelayInstanceIds),
                ["drainingRelayEndpoints"] = JArray.FromObject(_drainingRelayEndpoints),
                ["activatedRegions"] = JArray.FromObject(_activatedRegions),
                ["activeRelayDeployments"] = JObject.FromObject(_activeRelayDeployments),
                ["rooms"] = JArray.FromObject(_rooms),
                ["roomToRegion"] = JObject.FromObject(_roomToRegion),
                ["roomToServerEndpoint"] = JObject.FromObject(_roomToServerEndpoint),
                ["emptyRoomSince"] = JObject.FromObject(_emptyRoomSince),
                ["roomInstanceIds"] = JObject.FromObject(_roomInstanceIds),
                ["roomCountSequences"] = JObject.FromObject(_roomCountSequences),
                ["legacyDependencies"] = JArray.FromObject(_legacyDependencies)
            };
    }

    private static void ValidateSnapshot(JObject snapshot)
    {
        if (snapshot.Value<int?>("version") != 1)
            throw new InvalidOperationException("Unsupported balancer snapshot.");
        foreach (var name in new[] { "relayServers", "lastRelayInstanceIds", "retiredRelayInstanceIds",
                     "drainingRelayEndpoints", "activatedRegions", "activeRelayDeployments", "rooms", "roomToRegion", "roomToServerEndpoint", "emptyRoomSince",
                     "roomInstanceIds", "roomCountSequences", "legacyDependencies" })
            if (snapshot[name] == null)
                throw new InvalidOperationException($"Incomplete balancer snapshot: {name}.");
    }

    private static void RestoreSnapshot(JObject snapshot)
    {
        ValidateSnapshot(snapshot);
        lock (_relayServers)
        lock (_roomsLock)
        {
            _relayServers.Clear(); _relayServers.AddRange(snapshot["relayServers"]!.ToObject<RelayServer[]>()!);
            _rooms.Clear(); _rooms.AddRange(snapshot["rooms"]!.ToObject<RoomInfo[]>()!);
            RestoreDictionary(_lastRelayInstanceIds, snapshot, "lastRelayInstanceIds");
            RestoreDictionary(_retiredRelayInstanceIds, snapshot, "retiredRelayInstanceIds");
            RestoreDictionary(_roomToRegion, snapshot, "roomToRegion");
            RestoreDictionary(_roomToServerEndpoint, snapshot, "roomToServerEndpoint");
            RestoreDictionary(_emptyRoomSince, snapshot, "emptyRoomSince");
            RestoreDictionary(_roomInstanceIds, snapshot, "roomInstanceIds");
            RestoreDictionary(_roomCountSequences, snapshot, "roomCountSequences");
            _drainingRelayEndpoints.Clear(); _drainingRelayEndpoints.UnionWith(snapshot["drainingRelayEndpoints"]!.ToObject<string[]>()!);
            _activatedRegions.Clear(); _activatedRegions.UnionWith(snapshot["activatedRegions"]!.ToObject<string[]>()!);
            RestoreDictionary(_activeRelayDeployments, snapshot, "activeRelayDeployments");
            _legacyDependencies.Clear(); _legacyDependencies.AddRange(snapshot["legacyDependencies"]!.ToObject<LegacyDependency[]>()!);
        }
    }

    private static void RestoreDictionary<T>(Dictionary<string, T> target, JObject snapshot, string name)
    {
        var values = snapshot[name]!.ToObject<Dictionary<string, T>>()!;
        target.Clear();
        foreach (var pair in values) target.Add(pair.Key, pair.Value);
    }

    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException("Balancer peer URLs must be absolute HTTP(S) origins.");
        return value.TrimEnd('/');
    }

    private static JObject ParseObject(ApiResponse response) => JObject.Parse(Encoding.UTF8.GetString(response.data));

    private static void RequireSuccess(ApiResponse response)
    {
        if ((int)response.status is < 200 or >= 300)
        {
            if ((int)response.status >= 500 || response.status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException($"Balancer peer returned {(int)response.status}.", null, response.status);
            throw new InvalidOperationException($"Balancer peer returned {(int)response.status}: {Encoding.UTF8.GetString(response.data)}");
        }
    }

    private static async Task<ApiResponse> RequestJsonAsync(string origin, string path, JObject? body = null, bool localStatus = false)
    {
        using var request = new HttpRequestMessage(body == null ? System.Net.Http.HttpMethod.Get : System.Net.Http.HttpMethod.Post, origin + path);
        request.Headers.Add("internal_key_secret", Program.SECRET_INTERNAL);
        if (localStatus) request.Headers.Add("balancer_status_local", "1");
        if (body != null) request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        using var response = await DeploymentClient.SendAsync(request);
        return new ApiResponse(await response.Content.ReadAsByteArrayAsync(), response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? ContentType.JSON);
    }

    private static async Task<ApiResponse> ForwardAsync(string origin, HttpRequestBase incoming)
    {
        var query = Uri.TryCreate(incoming.Url.Full, UriKind.Absolute, out var sourceUri) ? sourceUri.Query : "";
        using var request = new HttpRequestMessage(new System.Net.Http.HttpMethod(incoming.Method.ToString()), origin + incoming.Url.RawWithoutQuery + query);
        foreach (var key in incoming.Headers?.AllKeys ?? [])
        {
            if (key == null || key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(key, incoming.Headers![key]);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        ArraySegment<byte> bytes;
        if (incoming.Url.RawWithoutQuery.StartsWith("/relay/", StringComparison.Ordinal) &&
            incoming.Url.RawWithoutQuery.EndsWith("/webrtc/offer", StringComparison.Ordinal))
        {
            var offer = await ReadOfferAsync(incoming, timeout.Token);
            if (!offer.HasValue) return new ApiResponse(HttpStatusCode.RequestEntityTooLarge);
            bytes = offer.Value;
        }
        else bytes = new ArraySegment<byte>(incoming.DataAsBytes ?? []);
        if (bytes.Count > 0)
        {
            request.Content = new ByteArrayContent(bytes.Array!, bytes.Offset, bytes.Count);
            if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(incoming.RetrieveHeaderValue("Content-Type"), out var contentType))
                request.Content.Headers.ContentType = contentType;
        }
        using var response = await DeploymentClient.SendAsync(request, timeout.Token);
        return new ApiResponse(await response.Content.ReadAsByteArrayAsync(), response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? ContentType.JSON);
    }

    private static ApiResponse PublicServers(IEnumerable<RelayServer> servers) =>
        new(new JObject { ["servers"] = JArray.FromObject(servers) });

    private static async Task<ApiResponse> RelayProxyAsync(HttpRequestBase req, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ping = parts.Length == 3 && parts[2] == "ping";
        var offer = parts.Length == 4 && parts[2] == "webrtc" && parts[3] == "offer";
        if ((!ping && !offer) || (ping && req.Method != WatsonWebserver.Core.HttpMethod.GET) ||
            (offer && req.Method != WatsonWebserver.Core.HttpMethod.POST))
            return new ApiResponse(HttpStatusCode.NotFound);
        RelayServer server;
        lock (StateGate)
        lock (_relayServers)
        {
            var matches = _relayServers.Where(candidate => candidate.instanceId == parts[1]).ToArray();
            if (matches.Length != 1) return new ApiResponse(HttpStatusCode.NotFound);
            server = matches[0];
        }
        using var request = new HttpRequestMessage(ping ? System.Net.Http.HttpMethod.Get : System.Net.Http.HttpMethod.Post,
            server.apiEndpoint.TrimEnd('/') + (ping ? "/ping" : "/webrtc/offer"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            if (offer)
            {
                var bytes = await ReadOfferAsync(req, timeout.Token);
                if (!bytes.HasValue) return new ApiResponse(HttpStatusCode.RequestEntityTooLarge);
                request.Content = new ByteArrayContent(bytes.Value.Array!, bytes.Value.Offset, bytes.Value.Count);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }
            using var response = await DeploymentClient.SendAsync(request, timeout.Token);
            return new ApiResponse(await response.Content.ReadAsByteArrayAsync(timeout.Token), response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? ContentType.JSON);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            return ApiResponse.FromError("Relay is unavailable.", HttpStatusCode.BadGateway);
        }
    }

    private static async Task<ArraySegment<byte>?> ReadOfferAsync(HttpRequestBase request, CancellationToken cancellation)
    {
        const int maxOfferBytes = 128 * 1024;
        if (long.TryParse(request.RetrieveHeaderValue("Content-Length"), out var contentLength) && contentLength > maxOfferBytes)
            return null;
        var bytes = new byte[maxOfferBytes + 1];
        var count = 0;
        while (request.Data != null && count < bytes.Length)
        {
            var read = await request.Data.ReadAsync(bytes.AsMemory(count), cancellation);
            if (read == 0) break;
            count += read;
        }
        if (count > maxOfferBytes) return null;
        return new ArraySegment<byte>(bytes, 0, count);
    }
}

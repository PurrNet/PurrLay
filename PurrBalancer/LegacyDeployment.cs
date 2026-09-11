using System.Net;
using Newtonsoft.Json.Linq;
using WatsonWebserver.Core;

namespace PurrBalancer;

public static partial class HTTPRestAPI
{
    private static readonly HashSet<string> _activatedRegions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ActiveRelayDeployment> _activeRelayDeployments = new(StringComparer.Ordinal);

    private sealed class ActiveRelayDeployment
    {
        public string apiEndpoint = "";
        public string? deploymentId;
    }

    private static bool IsDesiredActiveRelay(RelayServer server) =>
        _activeRelayDeployments.TryGetValue(server.region, out var active) &&
        active.apiEndpoint == server.apiEndpoint && active.deploymentId == server.deploymentId;

    private static ApiResponse ServerRegistrationAccepted(RelayServer server) => new(new JObject
    {
        ["status"] = "ok", ["instanceId"] = server.instanceId, ["deploymentId"] = server.deploymentId,
        ["acceptingRooms"] = !server.draining
    });

    private static async Task BootstrapLegacyAsync(string predecessor)
    {
        var response = await RequestJsonAsync(predecessor, "/servers");
        RequireSuccess(response);
        var servers = ParseObject(response)["servers"]?.ToObject<RelayServer[]>()
            ?? throw new InvalidOperationException("Legacy balancer did not return a relay registry.");
        if (servers.Any(server => string.IsNullOrWhiteSpace(server.apiEndpoint) || string.IsNullOrWhiteSpace(server.region)) ||
            servers.GroupBy(server => server.region).Any(group => group.Count() != 1) ||
            servers.GroupBy(server => server.apiEndpoint).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Legacy relay ownership is ambiguous; predecessor must remain active.");
        foreach (var server in servers)
            NormalizeUrl(server.apiEndpoint);
        var dependency = new LegacyDependency { url = predecessor, relayServers = servers };
        var rooms = await ReadLegacyRoomsAsync(dependency);
        if (rooms.Any(room => !servers.Any(server => server.region == room.Value<string>("region"))))
            throw new InvalidOperationException("Legacy balancer lists a room without an identifiable relay.");
        lock (StateGate)
            _legacyDependencies.Add(dependency);
    }

    private static async Task<JArray> ReadLegacyRoomsAsync(LegacyDependency dependency)
    {
        var response = await RequestJsonAsync(dependency.url, "/list?page=0");
        RequireSuccess(response);
        var body = ParseObject(response);
        var rooms = body["results"] as JArray
            ?? throw new InvalidOperationException("Legacy balancer returned an invalid room list.");
        var total = body.Value<int?>("total") ?? rooms.Count;
        // Older PurrBalancer returns the entire tail; support paginated predecessors as well.
        for (var page = 1; rooms.Count < total; ++page)
        {
            var next = await RequestJsonAsync(dependency.url, $"/list?page={page}");
            RequireSuccess(next);
            var values = ParseObject(next)["results"] as JArray;
            if (values == null || values.Count == 0)
                throw new InvalidOperationException("Legacy room listing ended before its reported total.");
            foreach (var room in values) rooms.Add(room);
        }
        return rooms;
    }

    private static async Task<ApiResponse?> HandleLegacyAsync(HttpRequestBase req, string path)
    {
        LegacyDependency[] dependencies;
        lock (StateGate)
            dependencies = _legacyDependencies.ToArray();
        if (dependencies.Length == 0)
            return null;

        if (path == "/servers")
        {
            var servers = new List<RelayServer>();
            lock (StateGate)
            lock (_relayServers)
                servers.AddRange(_relayServers.Where(server => !server.draining)
                    .GroupBy(server => server.region).Select(group => group.First()));
            foreach (var dependency in dependencies)
            {
                foreach (var server in dependency.relayServers)
                {
                    bool activated;
                    lock (StateGate) activated = _activatedRegions.Contains(server.region);
                    if (!activated && !servers.Any(current => current.region == server.region))
                        servers.Add(server);
                }
            }
            return PublicServers(servers);
        }

        if (path is "/list" or "/getTotalConnections")
        {
            var merged = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var dependency in dependencies)
                foreach (var room in await ReadLegacyRoomsAsync(dependency))
                    merged[room.Value<string>("name")!] = (JObject)room;
            lock (StateGate)
            lock (_roomsLock)
                foreach (var room in _rooms)
                    merged[room.name] = JObject.FromObject(room);
            if (path == "/getTotalConnections")
                return new ApiResponse(new JObject { ["totalConnections"] = merged.Values.Sum(room => room.Value<int>("connectedPlayers")) });
            if (!int.TryParse(req.RetrieveQueryValue("page") ?? "0", out var page) || page < 0)
                return ApiResponse.FromError("Invalid page", HttpStatusCode.BadRequest);
            return new ApiResponse(new JObject
            {
                ["results"] = new JArray(merged.Values.Skip(page * 50)), ["total"] = merged.Count
            });
        }

        var name = req.RetrieveHeaderValue("name");
        bool localRoom;
        lock (StateGate)
        lock (_roomsLock)
            localRoom = name != null && _roomToServerEndpoint.ContainsKey(name);

        if (path is "/join" or "/migration/current" or "/migration/claim")
        {
            if (localRoom) return null;
            foreach (var dependency in dependencies)
            {
                if ((await ReadLegacyRoomsAsync(dependency)).Any(room => room.Value<string>("name") == name))
                    return await ForwardAsync(dependency.url, req);
            }
            return null;
        }
        if (path == "/allocate_ws")
        {
            var region = req.RetrieveHeaderValue("region");
            lock (StateGate)
                if (_activatedRegions.Contains(region) || TryGetServer(region, out _))
                    return null;
            var dependency = dependencies.FirstOrDefault(item => item.relayServers.Any(server => server.region == region));
            return dependency == null ? null : await ForwardAsync(dependency.url, req);
        }
        if (!IsRegistryMutation(path))
            return null;

        string? endpoint;
        if (path is "/registerServer" or "/unregisterServer")
            endpoint = JObject.Parse(req.DataAsString).Value<string>("apiEndpoint");
        else
            endpoint = req.RetrieveHeaderValue("relay_endpoint");
        var owner = dependencies.FirstOrDefault(dependency => dependency.relayServers.Any(server => server.apiEndpoint == endpoint));
        if (owner == null && string.IsNullOrEmpty(endpoint) && !localRoom)
        {
            foreach (var dependency in dependencies)
            {
                if ((await ReadLegacyRoomsAsync(dependency)).Any(room => room.Value<string>("name") == name) ||
                    path == "/registerRoom" && dependency.relayServers.Any(server => server.region == req.RetrieveHeaderValue("region")))
                {
                    owner = dependency;
                    break;
                }
            }
        }
        if (owner != null)
            return await ForwardAsync(owner.url, req);

        // The legacy registry remains the global name reservation authority. Its
        // existing endpoint/instance CAS also fences requests still reaching it.
        foreach (var dependency in dependencies)
        {
            var response = await ForwardAsync(dependency.url, req);
            if ((int)response.status is < 200 or >= 300)
                return response;
        }
        return null;
    }
}

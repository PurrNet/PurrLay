using System.Net;
using Newtonsoft.Json.Linq;
using PurrBalancer;
using WatsonWebserver.Core;

namespace PurrLay;

internal static class RelayDeployment
{
    static bool _draining;
    static bool _retired;
    static bool _manuallyDraining;
    static long _administrationRevision;
    static int _pendingOffers;
    static string? _deploymentId;

    internal static void Initialize(string? deploymentId, bool standby)
    {
        lock (Lobby.SyncRoot)
        {
            _deploymentId = string.IsNullOrWhiteSpace(deploymentId) ? null : deploymentId;
            _draining = standby;
            _retired = false;
            _manuallyDraining = false;
            _administrationRevision++;
        }
    }

    internal static string? DeploymentId { get { lock (Lobby.SyncRoot) return _deploymentId; } }
    internal static bool Draining { get { lock (Lobby.SyncRoot) return _draining; } }
    internal static bool Retired { get { lock (Lobby.SyncRoot) return _retired; } }
    internal static long AdministrationRevision { get { lock (Lobby.SyncRoot) return _administrationRevision; } }

    internal static void ApplyRegistrationResponse(JObject response, long administrationRevision)
    {
        lock (Lobby.SyncRoot)
        {
            // A heartbeat sent before an admin action must not undo that action.
            if (_retired || _manuallyDraining || administrationRevision != _administrationRevision ||
                string.IsNullOrEmpty(_deploymentId) || response["acceptingRooms"]?.Type != JTokenType.Boolean ||
                !response.Value<bool>("acceptingRooms") ||
                response.Value<string>("instanceId") != Program.ProcessInstanceId ||
                response.Value<string>("deploymentId") != _deploymentId)
                return;
            _draining = false;
        }
    }

    internal static bool TryBeginOffer()
    {
        lock (Lobby.SyncRoot)
        {
            if (_retired) return false;
            _pendingOffers++;
            return true;
        }
    }

    internal static void EndOffer()
    {
        lock (Lobby.SyncRoot) _pendingOffers--;
    }

    internal static JObject Status()
    {
        lock (Lobby.SyncRoot)
        {
            var (rooms, allocations) = Lobby.GetDeploymentCounts();
            var connections = Transport.GetTransportConnectionCount();
            var pipes = PipeRelay.ConnectionCount;
            return new JObject
            {
                ["instanceId"] = Program.ProcessInstanceId,
                ["deploymentId"] = _deploymentId,
                ["ready"] = !_retired && HTTPRestAPI.webServer is { IsReady: true } &&
                    HTTPRestAPI.udpServerV1 != null && HTTPRestAPI.udpServerV2 != null &&
                    (HTTPRestAPI.webRtcRuntime == null || HTTPRestAPI.webRtcRuntime.Available),
                ["draining"] = _draining,
                ["retired"] = _retired,
                ["roomCount"] = rooms,
                ["pendingAllocations"] = allocations,
                ["transportConnections"] = connections,
                ["pipeConnections"] = pipes,
                ["pendingOffers"] = _pendingOffers,
                ["canRetire"] = _draining && rooms == 0 && allocations == 0 && connections == 0 &&
                    pipes == 0 && _pendingOffers == 0
            };
        }
    }

    internal static ApiResponse Handle(HttpRequestBase request, string action)
    {
        if (!string.Equals(request.RetrieveHeaderValue("internal_key_secret"), Program.SECRET_INTERNAL, StringComparison.Ordinal))
            return ApiResponse.FromError("Invalid internal secret.", HttpStatusCode.Forbidden);
        if (request.Method != (action == "status" ? WatsonWebserver.Core.HttpMethod.GET : WatsonWebserver.Core.HttpMethod.POST))
            return new ApiResponse(HttpStatusCode.MethodNotAllowed);

        lock (Lobby.SyncRoot)
        {
            if (action == "status") return new ApiResponse(Status());
            if (!string.Equals(request.RetrieveHeaderValue("relay_instance_id"), Program.ProcessInstanceId, StringComparison.Ordinal))
                return ApiResponse.FromError("Relay instance changed.", HttpStatusCode.Conflict);
            switch (action)
            {
                case "activate":
                    if (_retired) return ApiResponse.FromError("Relay has retired.", HttpStatusCode.Conflict);
                    if (!(bool)Status()["ready"]!)
                        return ApiResponse.FromError("Relay is not ready.", HttpStatusCode.ServiceUnavailable);
                    _draining = false;
                    _manuallyDraining = false;
                    break;
                case "drain":
                    _draining = true;
                    _manuallyDraining = true;
                    break;
                case "retire":
                    if (!(bool)Status()["canRetire"]!)
                        return ApiResponse.FromError("Relay still has rooms or connections.", HttpStatusCode.Conflict);
                    _retired = true;
                    break;
            }
            _administrationRevision++;
            return new ApiResponse(Status());
        }
    }
}

internal sealed class RelayDrainingException() : Exception("Relay is draining and no longer accepts new rooms.");

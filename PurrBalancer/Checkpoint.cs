using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PurrBalancer;

public static partial class HTTPRestAPI
{
    private static string? _checkpointPath;
    private static string? _lastCheckpoint;
    private static bool _checkpointFailed;
    private static bool _checkpointLoaded;

    private static void LoadCheckpoint()
    {
        _checkpointPath = Env.TryGetValueOrDefault("BALANCER_STATE_PATH", "");
        if (string.IsNullOrWhiteSpace(_checkpointPath))
            return;
        try
        {
            _checkpointPath = Path.GetFullPath(_checkpointPath);
            if (!File.Exists(_checkpointPath))
            {
                PersistCheckpoint();
                return;
            }
            var checkpoint = JObject.Parse(File.ReadAllText(_checkpointPath));
            if (checkpoint.Value<int?>("version") != 1 || checkpoint["snapshot"] is not JObject snapshot ||
                string.IsNullOrEmpty(checkpoint.Value<string>("instanceId")))
                throw new InvalidOperationException("Invalid balancer checkpoint.");
            var phase = checkpoint.Value<string>("phase");
            var successor = checkpoint.Value<string>("successorUrl");
            var pendingPredecessor = checkpoint.Value<string>("pendingPredecessor");
            if (phase is not ("initializing" or "authority" or "forwarder") ||
                (phase == "forwarder") != !string.IsNullOrEmpty(successor) ||
                (phase == "initializing") != !string.IsNullOrEmpty(pendingPredecessor) ||
                phase != "initializing" && checkpoint.Value<string>("pendingHandoffId") != null ||
                successor != null && string.IsNullOrEmpty(checkpoint.Value<string>("successorInstanceId")))
                throw new InvalidOperationException("Invalid balancer checkpoint authority state.");
            if (successor != null) NormalizeUrl(successor);
            if (pendingPredecessor != null) NormalizeUrl(pendingPredecessor);
            lock (StateGate)
            {
                RestoreSnapshot(snapshot);
                _instanceId = checkpoint.Value<string>("instanceId")!;
                _successorUrl = checkpoint.Value<string>("successorUrl");
                _successorInstanceId = checkpoint.Value<string>("successorInstanceId");
                _handoffId = checkpoint.Value<string>("handoffId");
                _handoffTarget = checkpoint.Value<string>("handoffTarget");
                _handoffInstance = checkpoint.Value<string>("handoffInstance");
                _initializing = phase == "initializing";
                _pendingPredecessor = checkpoint.Value<string>("pendingPredecessor");
                _pendingHandoffId = checkpoint.Value<string>("pendingHandoffId");
                _committedSnapshot = _successorUrl == null ? null : (JObject)snapshot.DeepClone();
                _ready = !_initializing && _successorUrl == null;
                _readiness = _initializing
                    ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                    : CompletedReadiness();
                _lastCheckpoint = checkpoint.ToString(Formatting.None);
                _checkpointLoaded = !_initializing;
            }
        }
        catch (Exception error)
        {
            DisableForCheckpointFailure(error);
        }
    }

    private static void PersistCheckpoint()
    {
        if (string.IsNullOrWhiteSpace(_checkpointPath))
            return;
        var temporary = _checkpointPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            var checkpoint = new JObject
            {
                ["version"] = 1, ["instanceId"] = _instanceId,
                ["phase"] = _initializing ? "initializing" : _successorUrl != null ? "forwarder" : "authority",
                ["pendingPredecessor"] = _pendingPredecessor, ["pendingHandoffId"] = _pendingHandoffId,
                ["successorUrl"] = _successorUrl, ["successorInstanceId"] = _successorInstanceId,
                ["handoffId"] = _handoffId, ["handoffTarget"] = _handoffTarget, ["handoffInstance"] = _handoffInstance,
                ["snapshot"] = CaptureSnapshot()
            }.ToString(Formatting.None);
            if (checkpoint == _lastCheckpoint)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(_checkpointPath)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(checkpoint);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _checkpointPath, overwrite: true);
            _lastCheckpoint = checkpoint;
        }
        catch (Exception error)
        {
            DisableForCheckpointFailure(error);
            throw;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void DisableForCheckpointFailure(Exception error)
    {
        _checkpointFailed = true;
        _ready = false;
        _readiness = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _startupError = $"Balancer checkpoint failed: {error.Message}";
        Console.Error.WriteLine(_startupError);
    }
}

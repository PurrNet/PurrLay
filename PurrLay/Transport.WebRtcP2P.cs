using System.Text;
using System.Text.Json;

namespace PurrLay;

public static partial class Transport
{
    internal const int MaxWebRtcP2PJsonBytes = 48 * 1024;
    internal const int MaxWebRtcP2PSignalBytes = 32 * 1024;
    internal const int MaxWebRtcP2PPendingPerHost = 32;
    internal static TimeProvider WebRtcP2PClock = TimeProvider.System;
    static readonly Dictionary<int, WebRtcP2PPeer> _webRtcP2PPeers = new();
    static readonly Dictionary<string, WebRtcP2PAttempt> _pendingWebRtcP2P = new(StringComparer.Ordinal);
    static readonly Dictionary<int, WebRtcP2PAttempt> _pendingWebRtcClients = new();
    static readonly HashSet<int> _webRtcDirectClients = new();

    sealed class WebRtcP2PPeer
    {
        internal bool HostReady;
        internal long WindowStarted = WebRtcP2PClock.GetTimestamp();
        internal int Messages;
        internal int Bytes;
    }

    sealed class WebRtcP2PAttempt(ulong roomId, PlayerInfo host, PlayerInfo client)
    {
        internal readonly string Token = Guid.NewGuid().ToString("N");
        internal readonly ulong RoomId = roomId;
        internal readonly PlayerInfo Host = host;
        internal readonly PlayerInfo Client = client;
        internal bool HostReady;
        internal bool ClientReady;
        internal int HostMessages;
        internal int ClientMessages;
        internal int HostBytes;
        internal int ClientBytes;
        internal ITimer? Timer;
    }

    // All handshake state and sends use the room lock. In particular, a late signal
    // must never follow AUTHENTICATED: the client then receives unframed game packets.
    static bool TryStartWebRtcP2P(ulong roomId, PlayerInfo client)
    {
        if (!_roomToHost.TryGetValue(roomId, out var host) ||
            !_webRtcP2PPeers.TryGetValue(host.connId, out var hostPeer) || !hostPeer.HostReady ||
            !_webRtcP2PPeers.ContainsKey(client.connId) ||
            _pendingWebRtcP2P.Count >= 1024 ||
            _pendingWebRtcP2P.Values.Count(attempt => attempt.Host == host) >= MaxWebRtcP2PPendingPerHost)
            return false;

        // Existing native UDP hole punching wins when both peers support it.
        if (_natCapable.Contains(host.connId) && _natCapable.Contains(client.connId) &&
            HTTPRestAPI.IsUdpV2(host.connId) && HTTPRestAPI.IsUdpV2(client.connId))
            return false;

        var attempt = new WebRtcP2PAttempt(roomId, host, client);
        _pendingWebRtcP2P.Add(attempt.Token, attempt);
        _pendingWebRtcClients.Add(client.connId, attempt);
        SendWebRtcP2P(host, attempt, "introduce");
        SendWebRtcP2P(client, attempt, "introduce");
        attempt.Timer = WebRtcP2PClock.CreateTimer(_ =>
        {
            try
            {
                lock (_transportLock) CompleteWebRtcP2P(attempt, false);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"WebRTC P2P timeout cleanup failed: {exception.GetType().Name}");
                KickPlayer(client);
            }
        }, null, TimeSpan.FromSeconds(8), Timeout.InfiniteTimeSpan);
        return true;
    }

    static void ReceiveWebRtcP2PControl(PlayerInfo sender, ulong roomId, PlayerInfo host, ArraySegment<byte> data)
    {
        if (!_webRtcP2PPeers.TryGetValue(sender.connId, out var peer) || data.Count is 0 or > MaxWebRtcP2PJsonBytes)
            return;

        if (WebRtcP2PClock.GetElapsedTime(peer.WindowStarted) >= TimeSpan.FromSeconds(1))
        {
            peer.WindowStarted = WebRtcP2PClock.GetTimestamp();
            peer.Messages = peer.Bytes = 0;
        }
        if (++peer.Messages > 256 || (peer.Bytes += data.Count) > 2 * 1024 * 1024)
            return;

        try
        {
            using var json = JsonDocument.Parse(data.AsMemory(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("token", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
                return;

            var token = tokenValue.GetString();
            if (token == null || token.Length != 32 || !_pendingWebRtcP2P.TryGetValue(token, out var attempt) ||
                attempt.RoomId != roomId || attempt.Host != host || (sender != host && sender != attempt.Client) ||
                !_clientToRoom.TryGetValue(attempt.Client, out var clientRoom) || clientRoom != roomId)
                return;

            bool isHost = sender == host;
            ref int messages = ref (isHost ? ref attempt.HostMessages : ref attempt.ClientMessages);
            ref int bytes = ref (isHost ? ref attempt.HostBytes : ref attempt.ClientBytes);
            if (++messages > 96 || (bytes += data.Count) > 256 * 1024)
            {
                CompleteWebRtcP2P(attempt, false);
                return;
            }

            switch (typeValue.GetString())
            {
                case "signal":
                    if (!root.TryGetProperty("signal", out var signalValue) || signalValue.ValueKind != JsonValueKind.String)
                        return;
                    var signal = signalValue.GetString()!;
                    if (Encoding.UTF8.GetByteCount(signal) is 0 or > MaxWebRtcP2PSignalBytes)
                    {
                        CompleteWebRtcP2P(attempt, false);
                        return;
                    }
                    SendWebRtcP2P(isHost ? attempt.Client : attempt.Host, attempt, "signal", signal);
                    break;
                case "ready":
                    if (isHost) attempt.HostReady = true;
                    else attempt.ClientReady = true;
                    if (attempt.HostReady && attempt.ClientReady) CompleteWebRtcP2P(attempt, true);
                    break;
                case "failed":
                    CompleteWebRtcP2P(attempt, false);
                    break;
            }
        }
        catch (JsonException) { }
    }

    static void CompleteWebRtcP2P(WebRtcP2PAttempt attempt, bool direct)
    {
        if (!_pendingWebRtcP2P.Remove(attempt.Token)) return;
        _pendingWebRtcClients.Remove(attempt.Client.connId);
        attempt.Timer?.Dispose();
        if (!_roomToHost.TryGetValue(attempt.RoomId, out var host) || host != attempt.Host ||
            !_clientToRoom.TryGetValue(attempt.Host, out var hostRoom) || hostRoom != attempt.RoomId ||
            !_clientToRoom.TryGetValue(attempt.Client, out var clientRoom) || clientRoom != attempt.RoomId)
            return;

        if (direct) _webRtcDirectClients.Add(attempt.Client.connId);
        SendWebRtcP2P(attempt.Host, attempt, "commit", direct: direct);
        SendWebRtcP2P(attempt.Client, attempt, "commit", direct: direct);
        SendSingleCode(attempt.Client, SERVER_PACKET_TYPE.SERVER_AUTHENTICATED);
        SendClientsConnected(attempt.RoomId, attempt.Client);
    }

    static void SendWebRtcP2P(PlayerInfo receiver, WebRtcP2PAttempt attempt, string type, string? signal = null, bool direct = false)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type, token = attempt.Token, clientId = attempt.Client.connId, signal, direct
        }, WebRtcP2PJsonOptions);
        if (json.Length > MaxWebRtcP2PJsonBytes) return;
        var packet = new byte[json.Length + 1];
        packet[0] = (byte)SERVER_PACKET_TYPE.SERVER_WEBRTC_P2P;
        json.CopyTo(packet, 1);
        HTTPRestAPI.GetUdpServerForConnection(receiver.connId)?.SendOne(receiver.connId, packet, RELIABLE_ORDERED);
    }

    static readonly JsonSerializerOptions WebRtcP2PJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static void FinishPendingWebRtcP2PForHost(PlayerInfo host)
    {
        foreach (var attempt in _pendingWebRtcP2P.Values.Where(attempt => attempt.Host == host).ToArray())
            CompleteWebRtcP2P(attempt, false);
    }

    static void RemoveWebRtcP2PPeer(PlayerInfo peer)
    {
        _webRtcP2PPeers.Remove(peer.connId);
        _webRtcDirectClients.Remove(peer.connId);
        foreach (var attempt in _pendingWebRtcP2P.Values.Where(attempt => attempt.Host == peer || attempt.Client == peer).ToArray())
        {
            _pendingWebRtcP2P.Remove(attempt.Token);
            _pendingWebRtcClients.Remove(attempt.Client.connId);
            attempt.Timer?.Dispose();
            if (attempt.Client == peer) SendWebRtcP2P(attempt.Host, attempt, "commit");
        }
    }
}

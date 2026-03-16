using LiteNetLib;
using LiteNetLib.Utils;

namespace PurrLay;

/// <summary>
/// Handles pipe connections — simple connId-to-connId forwarding with no rooms or hosts.
/// Pipe clients send [targetConnId(4)][data] (WS) or [deliveryMethod(1)][targetConnId(4)][data] (UDP).
/// Relay forwards as [senderConnId(4)][data] to the target.
/// </summary>
public static class PipeRelay
{
    static readonly Dictionary<int, bool> _pipeClients = new(); // connId → isUdp
    static readonly object _pipeLock = new();
    static readonly NetDataWriter _writer = new();

    public static bool IsClient(int connId)
    {
        lock (_pipeLock)
            return _pipeClients.ContainsKey(connId);
    }

    public static void AddClient(PlayerInfo player)
    {
        lock (_pipeLock)
            _pipeClients[player.connId] = player.isUdp;
    }

    public static bool RemoveClient(int connId)
    {
        lock (_pipeLock)
            return _pipeClients.Remove(connId);
    }

    public static void OnData(PlayerInfo sender, ArraySegment<byte> data)
    {
        if (data.Array == null)
            return;

        int targetConnId;
        ArraySegment<byte> payload;
        var method = DeliveryMethod.ReliableOrdered;

        if (sender.isUdp)
        {
            // UDP: [deliveryMethod(1)] [targetConnId(4)] [data]
            if (data.Count < 6) return;
            method = (DeliveryMethod)data.Array[data.Offset];
            targetConnId = ReadInt(data.Array, data.Offset + 1);
            payload = new ArraySegment<byte>(data.Array, data.Offset + 5, data.Count - 5);
        }
        else
        {
            // WS: [targetConnId(4)] [data]
            if (data.Count < 5) return;
            targetConnId = ReadInt(data.Array, data.Offset);
            payload = new ArraySegment<byte>(data.Array, data.Offset + 4, data.Count - 4);
        }

        bool targetIsUdp;
        lock (_pipeLock)
        {
            if (!_pipeClients.TryGetValue(targetConnId, out targetIsUdp))
                return;
        }

        // Forward: [senderConnId(4)] [data]
        _writer.Reset();
        _writer.Put(sender.connId);
        if (payload.Array != null)
            _writer.Put(payload.Array, payload.Offset, payload.Count);

        var segment = _writer.AsReadOnlySpan();

        if (targetIsUdp)
            HTTPRestAPI.udpServer?.SendOne(targetConnId, segment, method);
        else
            HTTPRestAPI.webServer?.SendOne(targetConnId, segment);
    }

    static int ReadInt(byte[] data, int offset)
    {
        return data[offset]
             | data[offset + 1] << 8
             | data[offset + 2] << 16
             | data[offset + 3] << 24;
    }
}

using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using PurrBalancer;

namespace PurrLay;

public class UdpServer : INetLogger
{
    private readonly NetManager _server;
    private readonly EventBasedNetListener _serverListener;

    static readonly Dictionary<NetPeer, int> _localConnToGlobal = new();
    static readonly Dictionary<int, NetPeer> _globalConnToLocal = new();
    static readonly object _udpConnLock = new();

    public UdpServer(int port)
    {
        NetDebug.Logger = this;
        _serverListener = new EventBasedNetListener();

        _server = new NetManager(_serverListener)
        {
            UnconnectedMessagesEnabled = true,
            PingInterval = 900,
            UnsyncedEvents = true,
            DisconnectTimeout = 20000
        };

        _serverListener.ConnectionRequestEvent += OnServerConnectionRequest;
        _serverListener.PeerConnectedEvent += OnServerConnected;
        _serverListener.PeerDisconnectedEvent += OnServerDisconnected;
        _serverListener.NetworkReceiveEvent += OnServerData;

        if (Env.TryGetValue("FLY_PROCESS_GROUP", out _))
        {
            var addresses = Dns.GetHostAddresses("fly-global-services");
            var ipv4 = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                       ?? IPAddress.Any;
            var ipv6 = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                       ?? IPAddress.IPv6Any;
            Console.WriteLine($"START: IPv4: {ipv4}, IPv6: {ipv6}");
            _server.Start(ipv4, ipv6, port);
        }
        else _server.Start(port);
    }

    private static void OnServerConnectionRequest(ConnectionRequest request)
    {
        request.AcceptIfKey("PurrNet");
    }

    private static void OnServerConnected(NetPeer conn)
    {
        Console.WriteLine("Client connected to UDP");
        var global = Transport.ReserveConnId(true);
        lock (_udpConnLock)
        {
            _localConnToGlobal[conn] = global;
            _globalConnToLocal[global] = conn;
        }
    }

    private static void OnServerDisconnected(NetPeer connId, DisconnectInfo disconnectinfo)
    {
        try
        {
            int global;
            lock (_udpConnLock)
            {
                if (!_localConnToGlobal.Remove(connId, out global))
                    return;
                _globalConnToLocal.Remove(global);
            }
            Transport.OnClientLeft(new PlayerInfo(global, true));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error handling disconnect: {e.Message}\n{e.StackTrace}");
        }
    }

    private static void OnServerData(NetPeer connId, NetPacketReader reader, byte channel, DeliveryMethod deliverymethod)
    {
        try
        {
            var data = reader.GetRemainingBytesSegment();
            int globalId;
            lock (_udpConnLock)
            {
                if (!_localConnToGlobal.TryGetValue(connId, out globalId))
                    return;
            }
            Transport.OnServerReceivedData(new PlayerInfo(globalId, true), data);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error handling data: {e.Message}\n{e.StackTrace}");
        }
    }

    public void WriteNet(NetLogLevel level, string str, params object[] args)
    {
        Console.WriteLine($"{level}: {str}", args);
    }

    public void KickClient(int playerConnId)
    {
        NetPeer? peer;
        lock (_udpConnLock)
        {
            if (!_globalConnToLocal.Remove(playerConnId, out peer))
                return;
            _localConnToGlobal.Remove(peer);
        }
        _server.DisconnectPeer(peer);
    }

    public void SendOne(int valueConnId, ReadOnlySpan<byte> segment, DeliveryMethod method)
    {
        NetPeer? peer;
        lock (_udpConnLock)
        {
            if (!_globalConnToLocal.TryGetValue(valueConnId, out peer))
                return;
        }

        var mtu = peer.GetMaxSinglePacketSize(method);

        bool isReliable =
            method is DeliveryMethod.ReliableOrdered
                or DeliveryMethod.ReliableUnordered
                or DeliveryMethod.ReliableSequenced;

        bool requiresSinglePacket = method != DeliveryMethod.ReliableUnordered && method != DeliveryMethod.ReliableOrdered;
        bool isSplit = segment.Length > mtu;

        if (requiresSinglePacket && isSplit)
        {
            if (isReliable)
            {
                Console.WriteLine($"Warning: Sending {segment.Length} bytes over {method} UDP, MTU is {mtu}; upgrading to ReliableOrdered");
                peer.Send(segment, DeliveryMethod.ReliableOrdered);
            }
            else Console.Error.WriteLine($"Error sending data: Cannot send {segment.Length} bytes over {method} UDP, MTU is {mtu}");
        }

        try
        {
            peer.Send(segment, method);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error sending data: {e.Message}\n{e.StackTrace}");
        }
    }
}

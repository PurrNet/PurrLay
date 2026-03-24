using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace PurrLay;

public class UdpServerV1 : IUdpServer, INetLogger
{
    private readonly NetManager _server;
    private readonly EventBasedNetListener _serverListener;
    private readonly UdpServerCallbacks _callbacks;

    private readonly Dictionary<NetPeer, int> _localConnToGlobal = new();
    private readonly Dictionary<int, NetPeer> _globalConnToLocal = new();
    private readonly object _udpConnLock = new();

    public UdpServerV1(int port, UdpServerCallbacks callbacks)
    {
        _callbacks = callbacks;
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

        if (Environment.GetEnvironmentVariable("FLY_PROCESS_GROUP") != null)
        {
            var addresses = Dns.GetHostAddresses("fly-global-services");
            var ipv4 = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                       ?? IPAddress.Any;
            var ipv6 = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                       ?? IPAddress.IPv6Any;
            Console.WriteLine($"UdpV1 START: IPv4: {ipv4}, IPv6: {ipv6}");
            _server.Start(ipv4, ipv6, port);
        }
        else _server.Start(port);
    }

    private static void OnServerConnectionRequest(ConnectionRequest request)
    {
        request.AcceptIfKey("PurrNet");
    }

    private void OnServerConnected(NetPeer conn)
    {
        Console.WriteLine("Client connected to UDP (V1)");
        var global = _callbacks.ReserveConnId(true);
        lock (_udpConnLock)
        {
            _localConnToGlobal[conn] = global;
            _globalConnToLocal[global] = conn;
        }
    }

    private void OnServerDisconnected(NetPeer connId, DisconnectInfo disconnectinfo)
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
            _callbacks.OnClientLeft(new PlayerInfo(global, true));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error handling disconnect (V1): {e.Message}\n{e.StackTrace}");
        }
    }

    private void OnServerData(NetPeer connId, NetPacketReader reader, byte channel, DeliveryMethod deliverymethod)
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
            _callbacks.OnDataReceived(new PlayerInfo(globalId, true), data);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error handling data (V1): {e.Message}\n{e.StackTrace}");
        }
    }

    public void WriteNet(NetLogLevel level, string str, params object[] args)
    {
        Console.WriteLine($"LiteNetV1 {level}: {str}", args);
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

    public void SendOne(int valueConnId, ReadOnlySpan<byte> segment, byte deliveryMethod)
    {
        var method = (DeliveryMethod)deliveryMethod;

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
                Console.WriteLine($"Warning: V1 sending {segment.Length} bytes over {method} UDP, MTU is {mtu}; upgrading to ReliableOrdered");
                peer.Send(segment, DeliveryMethod.ReliableOrdered);
                return;
            }

            Console.Error.WriteLine($"Error sending data (V1): Cannot send {segment.Length} bytes over {method} UDP, MTU is {mtu}");
            return;
        }

        try
        {
            peer.Send(segment, method);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error sending data (V1): {e.Message}\n{e.StackTrace}");
        }
    }
}

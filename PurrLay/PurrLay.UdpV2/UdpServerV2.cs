using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace PurrLay;

public class UdpServerV2 : IUdpServer, INetLogger
{
    private readonly NetManager _server;
    private readonly EventBasedNetListener _serverListener;
    private readonly UdpServerCallbacks _callbacks;

    private readonly Dictionary<NetPeer, int> _localConnToGlobal = new();
    private readonly Dictionary<int, NetPeer> _globalConnToLocal = new();
    private readonly object _udpConnLock = new();

    // --- NAT punch mediation ---
    private readonly EventBasedNatPunchListener _natListener = new();
    private readonly Dictionary<string, PendingNatPeer> _pendingNat = new();
    private readonly object _natLock = new();

    /// <summary>How long a half-paired NAT introduce request lingers before being purged.</summary>
    private static readonly TimeSpan NatRequestTimeout = TimeSpan.FromSeconds(20);

    private readonly record struct PendingNatPeer(IPEndPoint Internal, IPEndPoint External, DateTime ReceivedAt);

    public UdpServerV2(int port, UdpServerCallbacks callbacks)
    {
        _callbacks = callbacks;
        NetDebug.Logger = this;
        _serverListener = new EventBasedNetListener();

        _server = new NetManager(_serverListener)
        {
            UnconnectedMessagesEnabled = true,
            PingInterval = 900,
            UnsyncedEvents = true,
            DisconnectTimeout = 20000,
            // Act as a NAT introduction mediator for peers that opt in.
            NatPunchEnabled = true
        };

        _natListener.NatIntroductionRequest += OnNatIntroductionRequest;
        _server.NatPunchModule.UnsyncedEvents = true;
        _server.NatPunchModule.Init(_natListener);

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
            Console.WriteLine($"UdpV2 START: IPv4: {ipv4}, IPv6: {ipv6}");
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
        Console.WriteLine("Client connected to UDP (V2)");
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
            Console.Error.WriteLine($"Error handling disconnect (V2): {e.Message}\n{e.StackTrace}");
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
            Console.Error.WriteLine($"Error handling data (V2): {e.Message}\n{e.StackTrace}");
        }
    }

    /// <summary>
    /// Mediator side of NAT hole-punching. Two peers that were told to punch (via
    /// SERVER_NAT_INTRODUCE) each send an introduce request carrying the same token.
    /// We pair them up and introduce them to each other so they can connect directly.
    /// </summary>
    private void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        try
        {
            PendingNatPeer? partner = null;
            var incoming = new PendingNatPeer(localEndPoint, remoteEndPoint, DateTime.UtcNow);

            lock (_natLock)
            {
                // Drop stale half-pairs so a missing partner never wedges a token.
                if (_pendingNat.Count > 0)
                {
                    var now = DateTime.UtcNow;
                    var stale = _pendingNat
                        .Where(kv => now - kv.Value.ReceivedAt > NatRequestTimeout)
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var key in stale)
                        _pendingNat.Remove(key);
                }

                if (_pendingNat.TryGetValue(token, out var existing))
                {
                    // Same peer re-sending while waiting — just refresh, don't self-pair.
                    if (existing.External.Equals(remoteEndPoint))
                    {
                        _pendingNat[token] = incoming;
                    }
                    else
                    {
                        partner = existing;
                        _pendingNat.Remove(token);
                    }
                }
                else
                {
                    _pendingNat[token] = incoming;
                }
            }

            if (partner is { } p)
            {
                _server.NatPunchModule.NatIntroduce(
                    p.Internal, p.External,
                    incoming.Internal, incoming.External,
                    token);
                Console.WriteLine($"NAT introduce paired token '{token}': {p.External} <-> {incoming.External}");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error handling NAT introduce request (V2): {e.Message}\n{e.StackTrace}");
        }
    }

    public void WriteNet(NetLogLevel level, string str, params object[] args)
    {
        Console.WriteLine($"LiteNetV2 {level}: {string.Format(str, args)}");
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
                Console.WriteLine($"Warning: V2 sending {segment.Length} bytes over {method} UDP, MTU is {mtu}; upgrading to ReliableOrdered");
                peer.Send(segment, DeliveryMethod.ReliableOrdered);
                return;
            }

            Console.Error.WriteLine($"Error sending data (V2): Cannot send {segment.Length} bytes over {method} UDP, MTU is {mtu}");
            return;
        }

        try
        {
            peer.Send(segment, method);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error sending data (V2): {e.Message}\n{e.StackTrace}");
        }
    }
}

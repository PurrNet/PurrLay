using System.Text;
using Newtonsoft.Json;

namespace PurrLay;

public static class Transport
{
    /// <summary>
    /// DeliveryMethod.ReliableOrdered byte value — same across LiteNetLib v1 and v2.
    /// </summary>
    public const byte RELIABLE_ORDERED = 2;

    static readonly Dictionary<PlayerInfo, ulong> _clientToRoom = new();
    static readonly Dictionary<ulong, List<PlayerInfo>> _roomToClients = new();
    static readonly Dictionary<ulong, PlayerInfo> _roomToHost = new();
    static readonly Dictionary<int, bool> _connToUDP = new();
    // connIds of peers that authenticated with `nat = true` (support NAT hole-punching).
    static readonly HashSet<int> _natCapable = new();
    static readonly object _transportLock = new();

    [ThreadStatic] static PacketWriter? _writerField;
    static PacketWriter _writer => _writerField ??= new PacketWriter();
    private static int _nextConnId = 1;

    public static bool TryGetRoomPlayerCount(ulong roomId, out int count)
    {
        count = default;
        lock (_transportLock)
        {
            return _roomToClients.TryGetValue(roomId, out var list) && (count = list.Count) > 0;
        }
    }

    public static int GetTotalConnectionCount()
    {
        lock (_transportLock)
        {
            return _clientToRoom.Count;
        }
    }

    public static int ReserveConnId(bool isUdp)
    {
        var connId = Interlocked.Increment(ref _nextConnId) - 1;
        lock (_transportLock)
        {
            _connToUDP[connId] = isUdp;
        }
        return connId;
    }

    public static void ReleaseRoomHostForMigration(ulong roomId)
    {
        PlayerInfo host;
        int count = -1;

        lock (_transportLock)
        {
            if (!_roomToHost.Remove(roomId, out host))
                return;

            _clientToRoom.Remove(host);

            if (_roomToClients.TryGetValue(roomId, out var clients))
            {
                clients.Remove(host);
                count = clients.Count;
            }
        }

        KickPlayer(host);

        if (count >= 0)
            Lobby.UpdateRoomPlayerCount(roomId, count);
    }

    static void KickPlayer(PlayerInfo player)
    {
        if (player.isUdp)
            HTTPRestAPI.GetUdpServerForConnection(player.connId)?.KickClient(player.connId);
        else HTTPRestAPI.webServer?.KickClient(player.connId);
    }

    static void SendClientsDisconnected(ulong roomId, PlayerInfo player)
    {
        PlayerInfo host;
        lock (_transportLock)
        {
            if (!_roomToHost.TryGetValue(roomId, out host))
                return;
        }

        _writer.Reset();
        _writer.Put((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_DISCONNECTED);
        _writer.Put(player.connId);

        var segment = _writer.AsReadOnlySpan();

        if (host.isUdp)
             HTTPRestAPI.GetUdpServerForConnection(host.connId)?.SendOne(host.connId, segment, RELIABLE_ORDERED);
        else HTTPRestAPI.webServer?.SendOne(host.connId, segment);
    }

    public static void OnServerReceivedData(PlayerInfo sender, ArraySegment<byte> data)
    {
        if (data.Array == null)
            return;

        // Pipe clients are routed separately — no rooms, no host
        if (PipeRelay.IsClient(sender.connId))
        {
            PipeRelay.OnData(sender, data);
            return;
        }

        bool authenticated;
        lock (_transportLock)
        {
            authenticated = _clientToRoom.ContainsKey(sender);
        }

        if (!authenticated)
        {
            TryToAuthenticate(sender, data);
            return;
        }

        ulong roomId;
        PlayerInfo hostId;
        lock (_transportLock)
        {
            if (!_clientToRoom.TryGetValue(sender, out roomId))
                return;

            if (!_roomToHost.TryGetValue(roomId, out hostId))
                return;
        }

        if (hostId == sender)
        {
            var type = (HOST_PACKET_TYPE)data.Array[data.Offset];
            var subData = new ArraySegment<byte>(data.Array, data.Offset + 1, data.Count - 1);

            if (subData.Array == null)
                return;

            switch (type)
            {
                case HOST_PACKET_TYPE.SEND_KEEPALIVE:
                    break;
                case HOST_PACKET_TYPE.SEND_ONE:
                {
                    const int metdataLength = sizeof(int);

                    int target = subData.Array[subData.Offset + 0] |
                                 subData.Array[subData.Offset + 1] << 8 |
                                 subData.Array[subData.Offset + 2] << 16 |
                                 subData.Array[subData.Offset + 3] << 24;

                    bool isUDP;
                    bool isValidTarget;
                    lock (_transportLock)
                    {
                        if (!_connToUDP.TryGetValue(target, out isUDP))
                            break;

                        isValidTarget = _clientToRoom.TryGetValue(new PlayerInfo(target, isUDP), out var room) && room == roomId;
                    }

                    if (!isValidTarget)
                        break;

                    ArraySegment<byte> rawData;
                    byte method = RELIABLE_ORDERED;

                    if (sender.isUdp)
                    {
                        rawData = new ArraySegment<byte>(
                            subData.Array, subData.Offset + metdataLength + 1, subData.Count - metdataLength - 1);
                        method = subData.Array[subData.Offset + 4];
                    }
                    else
                    {
                        rawData = new ArraySegment<byte>(
                            subData.Array, subData.Offset + metdataLength, subData.Count - metdataLength);
                    }

                    if (isUDP)
                    {
                        HTTPRestAPI.GetUdpServerForConnection(target)?.SendOne(target, rawData, method);
                    }
                    else
                    {
                        HTTPRestAPI.webServer?.SendOne(target, rawData);
                    }
                    break;
                }
                case HOST_PACKET_TYPE.KICK_PLAYER:
                {
                    if (subData.Count < sizeof(int))
                        break;

                    int target = subData.Array[subData.Offset + 0] |
                                 subData.Array[subData.Offset + 1] << 8 |
                                 subData.Array[subData.Offset + 2] << 16 |
                                 subData.Array[subData.Offset + 3] << 24;

                    PlayerInfo targetPlayer;
                    bool isValidTarget;
                    lock (_transportLock)
                    {
                        if (!_connToUDP.TryGetValue(target, out var isUDP))
                            break;

                        targetPlayer = new PlayerInfo(target, isUDP);

                        // Verify target is in the same room
                        if (!_clientToRoom.TryGetValue(targetPlayer, out var targetRoomId) || targetRoomId != roomId)
                            break;

                        // Verify target is not the host (host can't kick themselves)
                        if (_roomToHost.TryGetValue(roomId, out var host) && host.Equals(targetPlayer))
                            break;

                        isValidTarget = true;
                    }

                    if (!isValidTarget)
                        break;

                    // Release lock before calling external methods
                    KickPlayer(targetPlayer);
                    break;
                }
            }
        }
        else
        {
            // Client
            PlayerInfo host;
            lock (_transportLock)
            {
                if (!_roomToHost.TryGetValue(roomId, out host))
                    return;
            }

            if (data.Count <= 1)
                return;

            _writer.Reset();

            var connId = sender.connId;

            _writer.Put((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_DATA);
            _writer.Put(connId);

            if (sender.isUdp)
                 _writer.Put(data.Array, data.Offset + 1, data.Count - 1);
            else _writer.Put(data.Array, data.Offset, data.Count);

            var segment = _writer.AsReadOnlySpan();

            if (host.isUdp)
            {
                byte method = sender.isUdp ? data[0] : RELIABLE_ORDERED;
                HTTPRestAPI.GetUdpServerForConnection(host.connId)?.SendOne(host.connId, segment, method);
            }
            else
            {
                HTTPRestAPI.webServer?.SendOne(host.connId, segment);
            }
        }
    }

    public static void OnClientLeft(PlayerInfo conn)
    {
        // Always clean up connection tracking regardless of client type
        lock (_transportLock)
        {
            _connToUDP.Remove(conn.connId);
            _natCapable.Remove(conn.connId);
        }
        HTTPRestAPI.RemoveUdpVersionTracking(conn.connId);

        // Pipe clients are tracked separately — no room cleanup needed
        if (PipeRelay.RemoveClient(conn.connId))
            return;

        ulong roomId;
        bool isHost = false;
        List<PlayerInfo>? clientsList;

        lock (_transportLock)
        {
            if (!_clientToRoom.Remove(conn, out roomId))
                return;

            if (_roomToHost.TryGetValue(roomId, out var hostId) && hostId.Equals(conn))
            {
                // Remove room host
                _roomToHost.Remove(roomId);
                isHost = true;

                // Get clients list before removing
                if (_roomToClients.Remove(roomId, out clientsList))
                {
                    // Make a copy to avoid holding lock while kicking
                    clientsList = new List<PlayerInfo>(clientsList);
                }
            }
            else if (_roomToClients.TryGetValue(roomId, out clientsList))
            {
                // Make a copy of the list reference, we'll modify it outside the lock
                // Actually, we need to remove from the list, so we'll do it carefully
            }
        }

        if (isHost)
        {
            // Kick all clients and remove room
            if (clientsList != null)
            {
                for (var i = 0; i < clientsList.Count; i++)
                    KickPlayer(clientsList[i]);
            }

            Lobby.RemoveRoom(roomId);
        }
        else if (clientsList != null)
        {
            // Remove client from list, then notify outside the lock
            int count = -1;
            lock (_transportLock)
            {
                if (_roomToClients.TryGetValue(roomId, out var list))
                {
                    list.Remove(conn);
                    count = list.Count;
                }
            }

            if (count >= 0)
            {
                SendClientsDisconnected(roomId, conn);
                Lobby.UpdateRoomPlayerCount(roomId, count);
            }
        }
    }

    static void TryToAuthenticate(PlayerInfo player, ArraySegment<byte> data)
    {
        try
        {
            if (data.Array == null)
            {
                SendSingleCode(player, SERVER_PACKET_TYPE.SERVER_AUTHENTICATION_FAILED);
                Console.Error.WriteLine("Authentication failed: data is null");
                return;
            }

            var str = Encoding.UTF8.GetString(data.Array, data.Offset, data.Count);
            var auth = JsonConvert.DeserializeObject<ClientAuthenticate>(str);

            // Pipe connections — no room, no host, just forwarding
            if (auth.pipe)
            {
                PipeRelay.AddClient(player);

                _writer.Reset();
                _writer.Put((byte)SERVER_PACKET_TYPE.SERVER_PIPE_AUTHENTICATED);
                _writer.Put(player.connId);

                var segment = _writer.AsReadOnlySpan();
                if (player.isUdp)
                    HTTPRestAPI.GetUdpServerForConnection(player.connId)?.SendOne(player.connId, segment, RELIABLE_ORDERED);
                else HTTPRestAPI.webServer?.SendOne(player.connId, segment);
                return;
            }

            if (string.IsNullOrWhiteSpace(auth.roomName) || string.IsNullOrWhiteSpace(auth.clientSecret) ||
                !Lobby.TryGetRoom(auth.roomName, out var room) || room == null)
            {
                SendSingleCode(player, SERVER_PACKET_TYPE.SERVER_AUTHENTICATION_FAILED);
                Console.Error.WriteLine("Authentication failed: invalid room or secrets");
                return;
            }

            bool isHost = false;
            List<PlayerInfo>? existingClients = null;
            bool hadExistingClients = false;

            lock (_transportLock)
            {
                if (room.clientSecret == auth.clientSecret)
                {
                    _clientToRoom.Add(player, room.roomId);
                }
                else if (room.hostSecret == auth.clientSecret)
                {
                    _clientToRoom.Add(player, room.roomId);
                    _roomToHost.Add(room.roomId, player);
                    isHost = true;
                }
                else
                {
                    SendSingleCode(player, SERVER_PACKET_TYPE.SERVER_AUTHENTICATION_FAILED);
                    Console.Error.WriteLine("Authentication failed: secrets do not match");
                    return;
                }

                if (auth.nat)
                    _natCapable.Add(player.connId);

                if (!_roomToClients.TryGetValue(room.roomId, out var list))
                {
                    _roomToClients.Add(room.roomId, [player]);
                    Lobby.UpdateRoomPlayerCount(room.roomId, 1);
                }
                else
                {
                    hadExistingClients = true;
                    if (isHost)
                        existingClients = new List<PlayerInfo>(list);
                    list.Add(player);
                    Lobby.UpdateRoomPlayerCount(room.roomId, list.Count);
                }
            }

            // A client just joined — if both it and the host support NAT punch, introduce them
            // so they can attempt a direct P2P link.
            //
            // Ordering matters on BOTH sides:
            //  - To the host, SERVER_NAT_INTRODUCE is sent BEFORE SERVER_CLIENT_CONNECTED so the
            //    host learns a punch is pending before it decides how to route that connection.
            //  - To the client, it is sent BEFORE SERVER_AUTHENTICATED because once a client is
            //    "connected" its relay receive path treats every packet as unframed game data.
            // Both are reliable-ordered, so send-order == receive-order.
            //
            // Limitation: only the joining client is introduced, and only to a host that
            // is already present. A client that authenticates before the host has no
            // partner yet, and it cannot be introduced retroactively — once it receives
            // SERVER_AUTHENTICATED its receive path treats every packet as unframed game
            // data, so a later SERVER_NAT_INTRODUCE can't reach it. Such a client stays
            // relay-only for the whole session. Hosts normally authenticate first, so this
            // is an edge case; closing it would require a client-side protocol change.
            if (!isHost)
                TryStartNatIntroduce(room.roomId, player);

            // Send notifications outside the lock
            if (hadExistingClients)
            {
                if (isHost)
                {
                    if (existingClients is { Count: > 0 })
                        SendClientsConnected(room.roomId, existingClients);
                }
                else SendClientsConnected(room.roomId, player);
            }

            SendSingleCode(player, SERVER_PACKET_TYPE.SERVER_AUTHENTICATED);
        }
        catch(Exception e)
        {
            SendSingleCode(player, SERVER_PACKET_TYPE.SERVER_AUTHENTICATION_FAILED);
            Console.Error.WriteLine($"Authentication failed (exception): {e.Message}\n{e.StackTrace}");
        }
    }

    /// <summary>
    /// If the host and the freshly-joined client both support NAT punch and are both on
    /// UDP V2, send each a SERVER_NAT_INTRODUCE with a shared token. They will each ask the
    /// relay (acting as mediator) to introduce them, then attempt a direct P2P connection.
    /// </summary>
    static void TryStartNatIntroduce(ulong roomId, PlayerInfo client)
    {
        PlayerInfo host;
        lock (_transportLock)
        {
            if (!_roomToHost.TryGetValue(roomId, out host))
                return;

            if (!_natCapable.Contains(host.connId) || !_natCapable.Contains(client.connId))
                return;
        }

        // NAT hole-punching only works between two real UDP V2 sockets
        // (WebSocket peers and the older V1 server cannot punch).
        if (!host.isUdp || !client.isUdp)
            return;

        if (!HTTPRestAPI.IsUdpV2(host.connId) || !HTTPRestAPI.IsUdpV2(client.connId))
            return;

        // Token must be identical on both sides. It ENDS with the client connId so the
        // host can map the resulting P2P peer back to the correct relay connection
        // (the host parses the segment after the last '_').
        //
        // The random middle segment is a security boundary, not decoration: this token
        // also serves as the P2P connection-accept key on the host. roomId and connId are
        // sequential counters, so a "roomId_connId" token would be trivially guessable and
        // would let an attacker pair with / connect to the host in place of the real peer.
        var token = $"{roomId}_{Guid.NewGuid():N}_{client.connId}";
        SendNatIntroduce(host, token);
        SendNatIntroduce(client, token);
    }

    static void SendNatIntroduce(PlayerInfo player, string token)
    {
        _writer.Reset();
        _writer.Put((byte)SERVER_PACKET_TYPE.SERVER_NAT_INTRODUCE);

        var tokenBytes = Encoding.UTF8.GetBytes(token);
        _writer.Put(tokenBytes, 0, tokenBytes.Length);

        var segment = _writer.AsReadOnlySpan();
        HTTPRestAPI.GetUdpServerForConnection(player.connId)?.SendOne(player.connId, segment, RELIABLE_ORDERED);
    }

    static void SendSingleCode(PlayerInfo player, SERVER_PACKET_TYPE type)
    {
        _writer.Reset();
        _writer.Put((byte)type);

        var segment = _writer.AsReadOnlySpan();

        if (player.isUdp)
            HTTPRestAPI.GetUdpServerForConnection(player.connId)?.SendOne(player.connId, segment, RELIABLE_ORDERED);
        else HTTPRestAPI.webServer?.SendOne(player.connId, segment);
    }

    static void SendClientsConnected(ulong roomId, List<PlayerInfo> connected)
    {
        PlayerInfo connId;
        lock (_transportLock)
        {
            if (!_roomToHost.TryGetValue(roomId, out connId))
                return;
        }

        _writer.Reset();
        _writer.Put((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_CONNECTED);

        for (int i = 0; i < connected.Count; i++)
        {
            var player = connected[i];
            _writer.Put(player.connId);
        }

        var segment = _writer.AsReadOnlySpan();
        if (connId.isUdp)
             HTTPRestAPI.GetUdpServerForConnection(connId.connId)?.SendOne(connId.connId, segment, RELIABLE_ORDERED);
        else HTTPRestAPI.webServer?.SendOne(connId.connId, segment);
    }

    static void SendClientsConnected(ulong roomId, PlayerInfo connected)
    {
        PlayerInfo connId;
        lock (_transportLock)
        {
            if (!_roomToHost.TryGetValue(roomId, out connId))
                return;
        }

        _writer.Reset();
        _writer.Put((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_CONNECTED);
        _writer.Put(connected.connId);

        var segment = _writer.AsReadOnlySpan();
        if (connId.isUdp)
            HTTPRestAPI.GetUdpServerForConnection(connId.connId)?.SendOne(connId.connId, segment, RELIABLE_ORDERED);
        else HTTPRestAPI.webServer?.SendOne(connId.connId, segment);
    }
}

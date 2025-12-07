using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;

namespace PurrLay;

public static class Transport
{
    static readonly Dictionary<PlayerInfo, ulong> _clientToRoom = new();
    static readonly Dictionary<ulong, List<PlayerInfo>> _roomToClients = new();
    static readonly Dictionary<ulong, PlayerInfo> _roomToHost = new();
    static readonly Dictionary<int, bool> _connToUDP = new();
    static readonly object _transportLock = new();

    static readonly NetDataWriter _writer = new();
    private static int _nextConnId;

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

    static void KickPlayer(PlayerInfo player)
    {
        if (player.isUdp)
            HTTPRestAPI.udpServer?.KickClient(player.connId);
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
             HTTPRestAPI.udpServer?.SendOne(host.connId, segment, DeliveryMethod.ReliableOrdered);
        else HTTPRestAPI.webServer?.SendOne(host.connId, segment);
    }

    public static void OnServerReceivedData(PlayerInfo sender, ArraySegment<byte> data)
    {
        if (data.Array == null)
            return;

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
                    var method = DeliveryMethod.ReliableOrdered;

                    if (sender.isUdp)
                    {
                        rawData = new ArraySegment<byte>(
                            subData.Array, subData.Offset + metdataLength + 1, subData.Count - metdataLength - 1);
                        method = (DeliveryMethod)subData.Array[subData.Offset + 4];
                    }
                    else
                    {
                        rawData = new ArraySegment<byte>(
                            subData.Array, subData.Offset + metdataLength, subData.Count - metdataLength);
                    }

                    if (isUDP)
                    {
                        HTTPRestAPI.udpServer?.SendOne(target, rawData, method);
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
                    bool isValidTarget = false;
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

                    // Remove player from room and kick them
                    int playerCount = 0;
                    lock (_transportLock)
                    {
                        if (!_clientToRoom.Remove(targetPlayer, out _))
                            break;

                        if (_roomToClients.TryGetValue(roomId, out var list))
                        {
                            list.Remove(targetPlayer);
                            playerCount = list.Count;
                        }
                    }

                    // Release lock before calling external methods
                    KickPlayer(targetPlayer);
                    // SendClientsDisconnected(roomId, targetPlayer);
                    // Lobby.UpdateRoomPlayerCount(roomId, playerCount);
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
                var method = sender.isUdp ? (DeliveryMethod)data[0] : DeliveryMethod.ReliableOrdered;
                HTTPRestAPI.udpServer?.SendOne(host.connId, segment, method);
            }
            else
            {
                HTTPRestAPI.webServer?.SendOne(host.connId, segment);
            }
        }
    }

    public static void OnClientLeft(PlayerInfo conn)
    {
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
            // Remove client from list
            lock (_transportLock)
            {
                if (_roomToClients.TryGetValue(roomId, out var list))
                {
                    list.Remove(conn);
                    var count = list.Count;
                    // Release lock before calling external method
                    SendClientsDisconnected(roomId, conn);
                    Lobby.UpdateRoomPlayerCount(roomId, count);
                }
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
                return;
            }

            var str = Encoding.UTF8.GetString(data.Array, data.Offset, data.Count);
            var auth = JsonConvert.DeserializeObject<ClientAuthenticate>(str);

            if (string.IsNullOrWhiteSpace(auth.roomName) || string.IsNullOrWhiteSpace(auth.clientSecret) ||
                !Lobby.TryGetRoom(auth.roomName, out var room) || room == null)
            {
                SendSingleCode(player, SERVER_PACKET_TYPE.SERVER_AUTHENTICATION_FAILED);
                return;
            }

            bool isHost = false;
            List<PlayerInfo>? existingClients = null;

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
                    return;
                }

                if (!_roomToClients.TryGetValue(room.roomId, out var list))
                {
                    _roomToClients.Add(room.roomId, [player]);
                    Lobby.UpdateRoomPlayerCount(room.roomId, 1);
                }
                else
                {
                    existingClients = list;
                    list.Add(player);
                    Lobby.UpdateRoomPlayerCount(room.roomId, list.Count);
                }
            }

            // Send notifications outside the lock
            if (existingClients != null)
            {
                if (isHost)
                     SendClientsConnected(room.roomId, existingClients);
                else SendClientsConnected(room.roomId, player);
            }

            SendSingleCode(player, SERVER_PACKET_TYPE.SERVER_AUTHENTICATED);
        }
        catch
        {
            SendSingleCode(player, SERVER_PACKET_TYPE.SERVER_AUTHENTICATION_FAILED);
        }
    }

    static void SendSingleCode(PlayerInfo player, SERVER_PACKET_TYPE type)
    {
        _writer.Reset();
        _writer.Put((byte)type);

        var segment = _writer.AsReadOnlySpan();

        if (player.isUdp)
            HTTPRestAPI.udpServer?.SendOne(player.connId, segment, DeliveryMethod.ReliableOrdered);
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
             HTTPRestAPI.udpServer?.SendOne(connId.connId, segment, DeliveryMethod.ReliableOrdered);
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
            HTTPRestAPI.udpServer?.SendOne(connId.connId, segment, DeliveryMethod.ReliableOrdered);
        else HTTPRestAPI.webServer?.SendOne(connId.connId, segment);
    }
}

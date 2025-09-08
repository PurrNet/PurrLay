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

    static readonly NetDataWriter _writer = new();
    private static int _nextConnId;

    public static int ReserveConnId(bool isUdp)
    {
        var connId = _nextConnId++;
        _connToUDP[connId] = isUdp;
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
        if (!_roomToHost.TryGetValue(roomId, out var host))
            return;

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

        bool authenticated = _clientToRoom.ContainsKey(sender);

        if (!authenticated)
        {
            TryToAuthenticate(sender, data);
            return;
        }

        if (!_clientToRoom.TryGetValue(sender, out var roomId))
            return;

        if (!_roomToHost.TryGetValue(roomId, out var hostId))
            return;

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

                    if (!_connToUDP.TryGetValue(target, out var isUDP))
                        break;

                    if (_clientToRoom.TryGetValue(new PlayerInfo(target, isUDP), out var room) && room == roomId)
                    {
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
                    }
                    break;
                }
            }
        }
        else
        {
            // Client
            if (_roomToHost.TryGetValue(roomId, out var host))
            {
                if (data.Count <= 1)
                    return;

                _writer.Reset();

                var connId = sender.connId;

                _writer.Put((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_DATA);
                _writer.Put(connId);

                if (sender.isUdp)
                     _writer.Put(data.Array, data.Offset + 1, data.Count - 1);
                else _writer.Put(data.Array, data.Offset, data.Count);

                if (host.isUdp)
                {
                    var method = sender.isUdp ? (DeliveryMethod)data[0] : DeliveryMethod.ReliableOrdered;
                    HTTPRestAPI.udpServer?.SendOne(host.connId, _writer.AsReadOnlySpan(), method);
                }
                else
                {
                    HTTPRestAPI.webServer?.SendOne(host.connId, _writer.AsReadOnlySpan());
                }
            }
        }
    }

    public static async Task OnClientLeft(PlayerInfo conn)
    {
        if (_clientToRoom.Remove(conn, out var roomId))
        {
            if (_roomToHost.TryGetValue(roomId, out var hostId) && hostId.Equals(conn))
            {
                // Remove room host
                _roomToHost.Remove(roomId);

                // Kick all clients and remove room
                if (_roomToClients.Remove(roomId, out var list))
                {
                    for (var i = 0; i < list.Count; i++)
                        KickPlayer(list[i]);
                }

                await Lobby.RemoveRoom(roomId);
            }
            else if (_roomToClients.TryGetValue(roomId, out var list))
            {
                SendClientsDisconnected(roomId, conn);
                list.Remove(conn);
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
                _roomToClients.Add(room.roomId, [player]);
            else
            {
                if (isHost)
                     SendClientsConnected(room.roomId, list);
                else SendClientsConnected(room.roomId, player);
                list.Add(player);
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
        if (!_roomToHost.TryGetValue(roomId, out var connId))
            return;

        _writer.Reset();
        _writer.Put((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_CONNECTED);

        for (int i = 0; i < connected.Count; i++)
        {
            var player = connected[i];
            _writer.Put(player.connId);
        }

        if (connId.isUdp)
             HTTPRestAPI.udpServer?.SendOne(connId.connId, _writer.AsReadOnlySpan(), DeliveryMethod.ReliableOrdered);
        else HTTPRestAPI.webServer?.SendOne(connId.connId, _writer.AsReadOnlySpan());
    }

    static void SendClientsConnected(ulong roomId, PlayerInfo connected)
    {
        if (!_roomToHost.TryGetValue(roomId, out var connId))
            return;

        _writer.Reset();
        _writer.Put((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_CONNECTED);
        _writer.Put(connected.connId);

        if (connId.isUdp)
            HTTPRestAPI.udpServer?.SendOne(connId.connId, _writer.AsReadOnlySpan(), DeliveryMethod.ReliableOrdered);
        else HTTPRestAPI.webServer?.SendOne(connId.connId, _writer.AsReadOnlySpan());
    }
}

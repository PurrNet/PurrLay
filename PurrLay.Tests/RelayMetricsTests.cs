using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace PurrLay.Tests;

public sealed class RelayMetricsTests
{
    [Fact]
    public async Task UdpRoomTrafficIsCountedOnceAndOnlyWhenRelayed()
    {
        await using var host = await RoomTestHost.StartAsync();
        using var metrics = new MetricsRecorder();
        var hostSecret = await Lobby.CreateRoom(host.Region, host.Name);
        var room = host.CurrentRoom();
        var hostPlayer = host.Authenticate(hostSecret);
        var a = host.Authenticate(room.clientSecret!);
        var b = host.Authenticate(room.clientSecret!);
        await host.WaitForCountAsync(3);

        Assert.Equal(3, metrics.Sum("purrlay.player.joins"));
        Assert.Equal(3, metrics.Gauge("purrlay.player.count"));
        Assert.Equal(1, metrics.Gauge("purrlay.room.count"));
        Assert.Equal(0, metrics.Sum("purrlay.relayed"));

        var receivedBefore = metrics.Sum("purrlay.received");
        var packetsBefore = host.Udp.Packets.Count;

        // Client to host: [delivery method][payload]. Host to client: [SEND_ONE][target id][delivery method][payload].
        byte[] fromClient = [Transport.RELIABLE_ORDERED, 1, 2, 3, 4, 5];
        Transport.OnServerReceivedData(a, fromClient);
        var toClient = SendOne(b.connId, [9, 9, 9]);
        Transport.OnServerReceivedData(hostPlayer, toClient);

        var relayed = host.Udp.Packets.Skip(packetsBefore).ToArray();
        Assert.Equal(2, relayed.Length);
        Assert.Equal(hostPlayer.connId, relayed[0].Connection);
        Assert.Equal(b.connId, relayed[1].Connection);
        Assert.Equal(relayed.Sum(p => (long)p.Data.Length), metrics.Sum("purrlay.relayed"));
        Assert.Equal(fromClient.Length + toClient.Length, metrics.Sum("purrlay.received") - receivedBefore);

        // Received but not relayed: a target outside the room, an empty client message, a keepalive.
        var relayedBefore = metrics.Sum("purrlay.relayed");
        Transport.OnServerReceivedData(hostPlayer, SendOne(int.MaxValue, [1]));
        Transport.OnServerReceivedData(a, new byte[] { Transport.RELIABLE_ORDERED });
        Transport.OnServerReceivedData(hostPlayer, new byte[] { (byte)HOST_PACKET_TYPE.SEND_KEEPALIVE });
        Assert.Equal(relayedBefore, metrics.Sum("purrlay.relayed"));
        Assert.Equal(packetsBefore + 2, host.Udp.Packets.Count);

        // Leaving and failed sign-ins change the players, never the joins.
        Transport.OnClientLeft(a);
        await host.WaitForCountAsync(2);
        Assert.Equal(2, metrics.Gauge("purrlay.player.count"));
        host.Authenticate("not-the-secret", success: false);
        Assert.Equal(3, metrics.Sum("purrlay.player.joins"));
    }

    [Fact]
    public void PipeRelayTrafficIsCounted()
    {
        var original = HTTPRestAPI.udpServerV1;
        var recorder = new RoomTestHost.RecordingUdpServer();
        HTTPRestAPI.udpServerV1 = recorder;
        var callbacks = HTTPRestAPI.CreateCallbacks(1);
        var a = new PlayerInfo(callbacks.ReserveConnId(true), true);
        var b = new PlayerInfo(callbacks.ReserveConnId(true), true);
        using var metrics = new MetricsRecorder();
        try
        {
            callbacks.OnDataReceived(a, Encoding.UTF8.GetBytes("{\"pipe\":true}"));
            callbacks.OnDataReceived(b, Encoding.UTF8.GetBytes("{\"pipe\":true}"));
            recorder.Packets.Clear();

            // Pipe over UDP: [delivery method][target id][payload]; forwarded as [sender id][payload].
            var data = new byte[8];
            data[0] = Transport.RELIABLE_ORDERED;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1, 4), b.connId);
            data[5] = 7;
            data[6] = 8;
            data[7] = 9;
            callbacks.OnDataReceived(a, data);

            var packet = Assert.Single(recorder.Packets);
            Assert.Equal(packet.Data.Length, metrics.Sum("purrlay.relayed"));
            Assert.Equal(0, metrics.Sum("purrlay.player.joins"));
        }
        finally
        {
            callbacks.OnClientLeft(a);
            callbacks.OnClientLeft(b);
            HTTPRestAPI.udpServerV1 = original;
        }
    }

    [Fact]
    public async Task WebSocketRoomTrafficIsCountedAsSentOverTheWire()
    {
        await using var host = await RoomTestHost.StartAsync();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var roomServer = HTTPRestAPI.webServer;
        using var server = new WebSockets(port);
        HTTPRestAPI.webServer = server;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ClientWebSocket? hostSocket = null, clientSocket = null;
        try
        {
            var hostSecret = await Lobby.CreateRoom(host.Region, host.Name);
            var room = host.CurrentRoom();
            using var metrics = new MetricsRecorder();

            hostSocket = await ConnectAsync(port, timeout.Token);
            await AuthenticateAsync(hostSocket, host.Name, hostSecret, timeout.Token);
            clientSocket = await ConnectAsync(port, timeout.Token);
            await AuthenticateAsync(clientSocket, host.Name, room.clientSecret!, timeout.Token);
            var connected = await ReceiveAsync(hostSocket, timeout.Token);
            Assert.Equal((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_CONNECTED, connected[0]);
            var clientId = BinaryPrimitives.ReadInt32LittleEndian(connected.AsSpan(1));
            Assert.Equal(2, metrics.Sum("purrlay.player.joins"));

            var receivedBefore = metrics.Sum("purrlay.received");

            // WebSocket client to host: the payload as is; the host gets [SERVER_CLIENT_DATA][client id][payload].
            byte[] fromClient = [1, 2, 3, 4];
            await clientSocket.SendAsync(fromClient, WebSocketMessageType.Binary, true, timeout.Token);
            var atHost = await ReceiveAsync(hostSocket, timeout.Token);
            Assert.Equal((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_DATA, atHost[0]);

            // WebSocket host to client: [SEND_ONE][target id][payload]; the client gets the payload.
            var toClient = new byte[1 + 4 + 3];
            toClient[0] = (byte)HOST_PACKET_TYPE.SEND_ONE;
            BinaryPrimitives.WriteInt32LittleEndian(toClient.AsSpan(1, 4), clientId);
            toClient[5] = 5;
            toClient[6] = 6;
            toClient[7] = 7;
            await hostSocket.SendAsync(toClient, WebSocketMessageType.Binary, true, timeout.Token);
            var atClient = await ReceiveAsync(clientSocket, timeout.Token);
            Assert.Equal(new byte[] { 5, 6, 7 }, atClient);

            Assert.Equal(atHost.Length + atClient.Length, metrics.Sum("purrlay.relayed"));
            Assert.Equal(fromClient.Length + toClient.Length, metrics.Sum("purrlay.received") - receivedBefore);
            Assert.Equal(2, metrics.Gauge("purrlay.player.count"));

            await clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            await host.WaitForCountAsync(1);
            Assert.Equal(1, metrics.Gauge("purrlay.player.count"));
        }
        finally
        {
            hostSocket?.Abort();
            clientSocket?.Abort();
            hostSocket?.Dispose();
            clientSocket?.Dispose();
            await RoomTestHost.WaitUntilAsync(() => Transport.GetTotalConnectionCount() == 0);
            HTTPRestAPI.webServer = roomServer;
        }
    }

    [Fact]
    public void NothingIsExportedWithoutAnEndpoint()
    {
        var original = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        try
        {
            RelayMetrics.Start();
            Assert.Null(RoomTestHost.ReadState<object?>(typeof(RelayMetrics), "_provider"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", original);
        }
    }

    static byte[] SendOne(int target, byte[] payload)
    {
        var data = new byte[1 + 4 + 1 + payload.Length];
        data[0] = (byte)HOST_PACKET_TYPE.SEND_ONE;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1, 4), target);
        data[5] = Transport.RELIABLE_ORDERED;
        payload.CopyTo(data, 6);
        return data;
    }

    static async Task<ClientWebSocket> ConnectAsync(int port, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var client = new ClientWebSocket();
            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), token);
                return client;
            }
            catch (WebSocketException) when (!token.IsCancellationRequested)
            {
                client.Dispose();
                await Task.Delay(20, token);
            }
        }
    }

    static async Task AuthenticateAsync(ClientWebSocket client, string roomName, string secret, CancellationToken token)
    {
        var auth = new JObject { ["roomName"] = roomName, ["clientSecret"] = secret }.ToString();
        await client.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Binary, true, token);
        var reply = await ReceiveAsync(client, token);
        Assert.Equal((byte)SERVER_PACKET_TYPE.SERVER_AUTHENTICATED, reply[0]);
    }

    static async Task<byte[]> ReceiveAsync(ClientWebSocket client, CancellationToken token)
    {
        var buffer = new byte[1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return message.ToArray();
    }

    // Listens to the relay's meter: counters are summed, gauges read on demand.
    sealed class MetricsRecorder : IDisposable
    {
        readonly MeterListener _listener = new();
        readonly ConcurrentDictionary<string, long> _sums = new();
        readonly ConcurrentDictionary<string, long> _gauges = new();

        public MetricsRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RelayMetrics.METER)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => _sums.AddOrUpdate(instrument.Name, value, (_, sum) => sum + value));
            _listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => _gauges[instrument.Name] = value);
            _listener.Start();
        }

        public long Sum(string name) => _sums.GetValueOrDefault(name);

        public long Gauge(string name)
        {
            _listener.RecordObservableInstruments();
            return _gauges.GetValueOrDefault(name);
        }

        public void Dispose() => _listener.Dispose();
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace PurrLay.Tests;

public sealed class LegacyWebSocketTests
{
    [Fact]
    public async Task LegacyWebSocketPeersAuthenticateAndForwardTheirOriginalWireFormat()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var originalServer = HTTPRestAPI.webServer;
        using var server = new WebSockets(port);
        HTTPRestAPI.webServer = server;
        ClientWebSocket? a = null, b = null;
        int aId = 0, bId = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            a = await ConnectAsync(port, timeout.Token);
            b = await ConnectAsync(port, timeout.Token);
            aId = await AuthenticateAsync(a, timeout.Token);
            bId = await AuthenticateAsync(b, timeout.Token);
            Assert.NotEqual(aId, bId);
            Assert.True(PipeRelay.IsClient(aId));
            Assert.True(PipeRelay.IsClient(bId));

            await ForwardAsync(a, aId, b, bId, new byte[] { 9, 8, 7 }, timeout.Token);
            await ForwardAsync(b, bId, a, aId, new byte[] { 4, 3, 2, 1 }, timeout.Token);

            await a.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            await b.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            await RoomTestHost.WaitUntilAsync(() => !PipeRelay.IsClient(aId) && !PipeRelay.IsClient(bId));
        }
        finally
        {
            a?.Abort();
            b?.Abort();
            a?.Dispose();
            b?.Dispose();
            try
            {
                await RoomTestHost.WaitUntilAsync(() => !PipeRelay.IsClient(aId) && !PipeRelay.IsClient(bId));
            }
            finally
            {
                server.Dispose();
                HTTPRestAPI.webServer = originalServer;
            }
        }
    }

    [Fact]
    public void LegacyV1ConnectionsStillDispatchAndForwardUsingTheirExistingBackend()
    {
        var original = HTTPRestAPI.udpServerV1;
        var recorder = new RoomTestHost.RecordingUdpServer();
        HTTPRestAPI.udpServerV1 = recorder;
        var callbacks = HTTPRestAPI.CreateCallbacks(1);
        var a = new PlayerInfo(callbacks.ReserveConnId(true), true);
        var b = new PlayerInfo(callbacks.ReserveConnId(true), true);
        try
        {
            Assert.Same(recorder, HTTPRestAPI.GetUdpServerForConnection(a.connId));
            Assert.Same(recorder, HTTPRestAPI.GetUdpServerForConnection(b.connId));
            Assert.False(HTTPRestAPI.IsUdpV2(a.connId));
            callbacks.OnDataReceived(a, Encoding.UTF8.GetBytes("{\"pipe\":true}"));
            callbacks.OnDataReceived(b, Encoding.UTF8.GetBytes("{\"pipe\":true}"));
            Assert.Equal(2, recorder.Packets.Count);
            Assert.All(recorder.Packets, packet =>
            {
                Assert.Equal((byte)SERVER_PACKET_TYPE.SERVER_PIPE_AUTHENTICATED, packet.Data[0]);
                Assert.Equal(Transport.RELIABLE_ORDERED, packet.Method);
            });
            recorder.Packets.Clear();
            var data = new byte[7];
            data[0] = 4;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1, 4), b.connId);
            data[5] = 8;
            data[6] = 9;
            callbacks.OnDataReceived(a, data);
            var packet = Assert.Single(recorder.Packets);
            Assert.Equal(b.connId, packet.Connection);
            Assert.Equal((byte)4, packet.Method);
            Assert.Equal(a.connId, BinaryPrimitives.ReadInt32LittleEndian(packet.Data));
            Assert.Equal(new byte[] { 8, 9 }, packet.Data[4..]);
        }
        finally
        {
            callbacks.OnClientLeft(a);
            callbacks.OnClientLeft(b);
            HTTPRestAPI.udpServerV1 = original;
        }
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
                await Task.Delay(20, token); // WebSockets starts its listener on a background thread.
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    static async Task<int> AuthenticateAsync(ClientWebSocket client, CancellationToken token)
    {
        await client.SendAsync(Encoding.UTF8.GetBytes("{\"pipe\":true}"), WebSocketMessageType.Binary, true, token);
        var reply = await ReceiveAsync(client, token);
        Assert.Equal(5, reply.Length);
        Assert.Equal((byte)SERVER_PACKET_TYPE.SERVER_PIPE_AUTHENTICATED, reply[0]);
        return BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1));
    }

    static async Task ForwardAsync(ClientWebSocket sender, int senderId, ClientWebSocket target,
        int targetId, byte[] payload, CancellationToken token)
    {
        // Legacy WebSocket frames have no delivery byte.
        var message = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(message, targetId);
        payload.CopyTo(message, 4);
        await sender.SendAsync(message, WebSocketMessageType.Binary, true, token);
        var received = await ReceiveAsync(target, token);
        Assert.Equal(senderId, BinaryPrimitives.ReadInt32LittleEndian(received));
        Assert.Equal(payload, received[4..]);
    }

    static async Task<byte[]> ReceiveAsync(ClientWebSocket client, CancellationToken token)
    {
        var buffer = new byte[1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return message.ToArray();
    }
}

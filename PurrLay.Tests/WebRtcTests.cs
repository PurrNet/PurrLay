using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace PurrLay.Tests;

public sealed class WebRtcTests
{
    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(2, 4)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(3, 4)]
    public async Task MixedNativeAndBrowserRoomPreservesDeliveryAndNeverIntroducesNativeNat(int hostBackend, byte method)
    {
        var originalNative = HTTPRestAPI.udpServerV2;
        var originalRtc = HTTPRestAPI.webRtcServer;
        var native = new RoomTestHost.RecordingUdpServer();
        var rtc = new RoomTestHost.RecordingUdpServer();
        HTTPRestAPI.udpServerV2 = native;
        HTTPRestAPI.webRtcServer = rtc;
        try
        {
            await using var room = await RoomTestHost.StartAsync();
            var secret = await Lobby.CreateRoom(room.Region, room.Name);
            var host = room.Authenticate(secret, backend: hostBackend, nat: true);
            var client = room.Authenticate(room.CurrentRoom().clientSecret!, backend: hostBackend == 2 ? 3 : 2, nat: true);
            var hostPackets = hostBackend == 2 ? native.Packets : rtc.Packets;
            var clientPackets = hostBackend == 2 ? rtc.Packets : native.Packets;
            Assert.Equal(hostBackend == 2, HTTPRestAPI.IsUdpV2(host.connId));
            Assert.Equal(hostBackend != 2, HTTPRestAPI.IsUdpV2(client.connId));
            Assert.All(native.Packets.Concat(rtc.Packets), packet => Assert.Equal(Transport.RELIABLE_ORDERED, packet.Method));
            Assert.DoesNotContain(native.Packets.Concat(rtc.Packets), packet => packet.Data[0] == (byte)SERVER_PACKET_TYPE.SERVER_NAT_INTRODUCE);
            hostPackets.Clear();
            clientPackets.Clear();

            Transport.OnServerReceivedData(client, new byte[] { method, 10, 20 });
            var toHost = Assert.Single(hostPackets);
            Assert.Equal(host.connId, toHost.Connection);
            Assert.Equal(method, toHost.Method);
            Assert.Equal((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_DATA, toHost.Data[0]);
            Assert.Equal(client.connId, BinaryPrimitives.ReadInt32LittleEndian(toHost.Data.AsSpan(1, 4)));
            Assert.Equal(new byte[] { 10, 20 }, toHost.Data[5..]);

            var toClient = new byte[8];
            toClient[0] = 1; // HOST_PACKET_TYPE.SEND_ONE
            BinaryPrimitives.WriteInt32LittleEndian(toClient.AsSpan(1, 4), client.connId);
            toClient[5] = method;
            toClient[6] = 30;
            toClient[7] = 40;
            Transport.OnServerReceivedData(host, toClient);
            var forwarded = Assert.Single(clientPackets);
            Assert.Equal(client.connId, forwarded.Connection);
            Assert.Equal(method, forwarded.Method);
            Assert.Equal(new byte[] { 30, 40 }, forwarded.Data);
        }
        finally
        {
            HTTPRestAPI.udpServerV2 = originalNative;
            HTTPRestAPI.webRtcServer = originalRtc;
        }
    }

    [Fact]
    public void BrowserAndNativePipeForwardWithoutChangingPayloadOrDelivery()
    {
        var originalNative = HTTPRestAPI.udpServerV2;
        var originalRtc = HTTPRestAPI.webRtcServer;
        var native = new RoomTestHost.RecordingUdpServer();
        var rtc = new RoomTestHost.RecordingUdpServer();
        HTTPRestAPI.udpServerV2 = native;
        HTTPRestAPI.webRtcServer = rtc;
        var a = new PlayerInfo(HTTPRestAPI.CreateCallbacks(2).ReserveConnId(true), true);
        var b = new PlayerInfo(HTTPRestAPI.CreateCallbacks(3).ReserveConnId(true), true);
        try
        {
            var auth = Encoding.UTF8.GetBytes("{\"pipe\":true}");
            Transport.OnServerReceivedData(a, auth);
            Transport.OnServerReceivedData(b, auth);
            Assert.Equal(Transport.RELIABLE_ORDERED, Assert.Single(rtc.Packets).Method);
            native.Packets.Clear();
            rtc.Packets.Clear();
            foreach (var (sender, target, recorder) in new[] { (a, b, rtc), (b, a, native) })
            {
                var data = new byte[7];
                data[0] = 4;
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1, 4), target.connId);
                data[5] = 9;
                data[6] = 8;
                Transport.OnServerReceivedData(sender, data);
                var packet = Assert.Single(recorder.Packets);
                Assert.Equal((byte)4, packet.Method);
                Assert.Equal(sender.connId, BinaryPrimitives.ReadInt32LittleEndian(packet.Data));
                Assert.Equal(new byte[] { 9, 8 }, packet.Data[4..]);
            }
        }
        finally
        {
            Transport.OnClientLeft(a);
            Transport.OnClientLeft(b);
            Assert.False(PipeRelay.IsClient(a.connId));
            Assert.False(PipeRelay.IsClient(b.connId));
            HTTPRestAPI.udpServerV2 = originalNative;
            HTTPRestAPI.webRtcServer = originalRtc;
        }
    }

    [Fact]
    public async Task BridgeAuthenticatesCopiesOutgoingPayloadAndLeavesExactlyOnceOnKick()
    {
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int leftCount = 0;
        using var server = new WebRtcServer(new IPEndPoint(IPAddress.Loopback, 0), "test-token", new UdpServerCallbacks
        {
            ReserveConnId = isUdp => { Assert.True(isUdp); return 42; },
            OnDataReceived = (player, data) => { Assert.Equal(42, player.connId); received.TrySetResult(data.ToArray()); },
            OnClientLeft = player => { Interlocked.Increment(ref leftCount); left.TrySetResult(); }
        });
        using var peer = await ConnectAsync(server);
        var stream = peer.GetStream();
        await WriteFrameAsync(stream, 0, 0, Encoding.UTF8.GetBytes("test-token"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hello = await WebRtcServer.ReadFrameAsync(stream, timeout.Token);
        Assert.Equal((byte)0, hello.Type);
        Assert.Empty(hello.Payload);
        await WriteFrameAsync(stream, 1, 2, Encoding.UTF8.GetBytes("{\"pipe\":true}"));
        Assert.Equal("{\"pipe\":true}", Encoding.UTF8.GetString(await received.Task.WaitAsync(timeout.Token)));

        // Oversized sequenced traffic must leave the peer connected for the next message.
        server.SendOne(42, new byte[WebRtcServer.MaxPayload - 3], 1);
        var payload = new byte[] { 1, 2, 3 };
        server.SendOne(42, payload, 4);
        payload[0] = 99;
        var frame = await WebRtcServer.ReadFrameAsync(stream, timeout.Token);
        Assert.Equal((byte)1, frame.Type);
        Assert.Equal((byte)4, frame.Method);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload);
        server.KickClient(42);
        server.KickClient(42);
        await left.Task.WaitAsync(timeout.Token);
        server.Dispose();
        Assert.Equal(1, leftCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BridgeRejectsUnauthenticatedOrOversizedInputBeforeReservingAPlayer(bool oversized)
    {
        int registered = 0, left = 0;
        using var server = new WebRtcServer(new IPEndPoint(IPAddress.Loopback, 0), "correct-token", new UdpServerCallbacks
        {
            ReserveConnId = _ => Interlocked.Increment(ref registered),
            OnDataReceived = (_, _) => Assert.Fail("Unauthenticated data reached the relay."),
            OnClientLeft = _ => Interlocked.Increment(ref left)
        });
        using var peer = await ConnectAsync(server);
        var stream = peer.GetStream();
        if (oversized)
        {
            var header = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), 65536);
            await stream.WriteAsync(header);
        }
        else await WriteFrameAsync(stream, 0, 0, Encoding.UTF8.GetBytes("wrong-token"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
        Assert.Equal(0, registered);
        Assert.Equal(0, left);
    }

    [Fact]
    public async Task BridgeCloseAndInvalidPostHandshakeFramesRemoveThePeer()
    {
        foreach (var oversized in new[] { false, true })
        {
            var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebRtcServer(new IPEndPoint(IPAddress.Loopback, 0), "token", new UdpServerCallbacks
            {
                ReserveConnId = _ => 1,
                OnDataReceived = (_, _) => Assert.Fail("Malformed frame reached the relay."),
                OnClientLeft = _ => left.TrySetResult()
            });
            using var peer = await ConnectAsync(server);
            var stream = peer.GetStream();
            await WriteFrameAsync(stream, 0, 0, Encoding.UTF8.GetBytes("token"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WebRtcServer.ReadFrameAsync(stream, timeout.Token);
            var header = new byte[6];
            header[0] = oversized ? (byte)1 : (byte)2;
            if (oversized) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), 65536);
            await stream.WriteAsync(header, timeout.Token);
            await left.Task.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task DisabledGatewayOmitsCapabilityAndOffersReturnUnavailableWithCorsPreflight()
    {
        await using var room = await RoomTestHost.StartAsync();
        await Lobby.CreateRoom(room.Region, room.Name);
        var joined = await room.JoinAsync();
        Assert.Null(joined.Property("webRtcUrl"));
        var preflight = await HTTPRestAPI.OnRequest(new TestHttpRequest("/webrtc/offer") { Method = WatsonWebserver.Core.HttpMethod.OPTIONS });
        Assert.Equal(HttpStatusCode.NoContent, preflight.status);
        var response = await HTTPRestAPI.OnRequest(new TestHttpRequest("/webrtc/offer") { Method = WatsonWebserver.Core.HttpMethod.POST });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.status);
    }

    [Fact]
    public async Task OfferProxyBoundsBodiesAndValidatesOfferAndAnswer()
    {
        int forwarded = 0;
        using var http = new HttpClient(new ResponseHandler(() =>
        {
            Interlocked.Increment(ref forwarded);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"type\":\"answer\",\"sdp\":\"v=0\"}") };
        }));
        var target = new Uri("http://127.0.0.1:8090/offer");
        foreach (var invalid in new[] { "not-json", "{\"type\":4,\"sdp\":\"x\"}", "{\"type\":\"answer\",\"sdp\":\"x\"}" })
        {
            var response = await WebRtcGatewayRuntime.ProxyOfferAsync(Offer(invalid), http, target);
            Assert.Equal(HttpStatusCode.BadRequest, response.status);
        }
        var tooLarge = await WebRtcGatewayRuntime.ProxyOfferAsync(Offer(new string('x', 128 * 1024 + 1)), http, target);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.status);
        Assert.Equal(0, forwarded);
        var good = await WebRtcGatewayRuntime.ProxyOfferAsync(Offer("{\"type\":\"offer\",\"sdp\":\"v=0\"}"), http, target);
        Assert.Equal(HttpStatusCode.OK, good.status);
        Assert.Equal("answer", (string?)JObject.Parse(Encoding.UTF8.GetString(good.data))["type"]);
        Assert.Equal(1, forwarded);

        using var badGateway = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string('x', 128 * 1024 + 1)) }));
        var rejected = await WebRtcGatewayRuntime.ProxyOfferAsync(Offer("{\"type\":\"offer\",\"sdp\":\"v=0\"}"), badGateway, target);
        Assert.Equal(HttpStatusCode.BadGateway, rejected.status);
    }

    [Fact]
    public void BridgeAddressesCannotExposeTheTrustedInternalProtocol()
    {
        Assert.Throws<ArgumentException>(() => WebRtcGatewayRuntime.ParseLoopbackEndpoint("0.0.0.0:8091"));
        Assert.Throws<ArgumentException>(() => WebRtcGatewayRuntime.ParseLoopbackEndpoint("192.168.1.2:8091"));
        Assert.Throws<ArgumentException>(() => new WebRtcServer(new IPEndPoint(IPAddress.Any, 8091), "token", null!));
        Assert.Throws<ArgumentException>(() => new WebRtcServer(new IPEndPoint(IPAddress.Loopback, 0), "", null!));
    }

    static TestHttpRequest Offer(string body) => new("/webrtc/offer")
    {
        Method = WatsonWebserver.Core.HttpMethod.POST,
        Data = new MemoryStream(Encoding.UTF8.GetBytes(body))
    };

    static async Task<TcpClient> ConnectAsync(WebRtcServer server)
    {
        var client = new TcpClient();
        await client.ConnectAsync(server.Endpoint);
        return client;
    }

    static async Task WriteFrameAsync(Stream stream, byte type, byte method, byte[] payload)
    {
        var frame = new byte[6 + payload.Length];
        frame[0] = type;
        frame[1] = method;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)payload.Length);
        payload.CopyTo(frame, 6);
        await stream.WriteAsync(frame);
    }

    sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }
}

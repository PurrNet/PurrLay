using System.Buffers.Binary;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace PurrLay.Tests;

public sealed class WebRtcP2PTests
{
    [Fact]
    public async Task IntroductionWaitsUntilHostAuthenticationNotificationHasBeenSent()
    {
        await using var test = await Harness.StartAsync();
        PlayerInfo earlyClient = default;
        test.Udp.BeforeSend = (_, packet) =>
        {
            if (packet[0] != (byte)SERVER_PACKET_TYPE.SERVER_AUTHENTICATED) return;
            test.Udp.BeforeSend = null;
            earlyClient = test.Room.Authenticate(test.Room.CurrentRoom().clientSecret!, backend: 3, webRtcP2P: true);
        };
        var host = test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
        Assert.NotEqual(default, earlyClient);
        Assert.Equal(new byte[] { 3 }, test.PacketsFor(earlyClient).Select(packet => packet[0]));
        Assert.DoesNotContain(test.Udp.Packets, packet => packet.Data[0] == 7);

        var readyClient = test.AddClient();
        Assert.Equal(new byte[] { 7 }, test.PacketsFor(readyClient).Select(packet => packet[0]));
        var hostPackets = test.PacketsFor(host).Select(packet => packet[0]).ToArray();
        Assert.True(Array.IndexOf(hostPackets, (byte)3) < Array.IndexOf(hostPackets, (byte)7));
        Assert.Single(test.Clock.Timers);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    public async Task BothPeersMustBeReadyBeforeDirectCommitAndGameNotifications(int hostBackend, int clientBackend)
    {
        await using var test = await Harness.StartAsync();
        var host = test.Room.Authenticate(test.HostSecret, backend: hostBackend, webRtcP2P: true);
        var client = test.AddClient(clientBackend);
        var token = test.Token(client);
        Assert.Equal(new byte[] { 7 }, test.PacketsFor(client).Select(packet => packet[0]));
        Assert.Equal(new byte[] { 3, 7 }, test.PacketsFor(host).Select(packet => packet[0]));
        test.Udp.Packets.Clear();

        Control(host, true, token, "signal", "{\"type\":\"offer\",\"sdp\":\"test\"}");
        var forwarded = Assert.Single(test.Udp.Packets);
        Assert.Equal(client.connId, forwarded.Connection);
        Assert.Equal(Transport.RELIABLE_ORDERED, forwarded.Method);
        Assert.Equal(client.connId, (int)Json(forwarded.Data)["clientId"]!);
        Assert.Equal("{\"type\":\"offer\",\"sdp\":\"test\"}", (string?)Json(forwarded.Data)["signal"]);
        test.Udp.Packets.Clear();

        Control(host, true, token, "ready");
        Assert.Empty(test.Udp.Packets);
        Control(client, false, token, "ready");
        Assert.Equal(new byte[] { 7, 3 }, test.PacketsFor(client).Select(packet => packet[0]));
        Assert.Equal(new byte[] { 7, 0 }, test.PacketsFor(host).Select(packet => packet[0]));
        Assert.All(test.Udp.Packets.Where(packet => packet.Data[0] == 7), packet =>
            Assert.True((bool)Json(packet.Data)["direct"]!));
        test.Udp.Packets.Clear();

        // Neither stale controls nor relay game packets can change a committed route.
        Control(client, false, token, "failed");
        Control(host, true, token, "signal", "late signal");
        Transport.OnServerReceivedData(client, new byte[] { 2, 10, 20 });
        Transport.OnServerReceivedData(host, HostData(client, 30, 40));
        test.Clock.FireAll();
        Assert.Empty(test.Udp.Packets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrDeadlineCommitsRelayOnceAndPreservesGameWire(bool timeout)
    {
        await using var test = await Harness.StartAsync();
        var host = test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
        var client = test.AddClient();
        var token = test.Token(client);
        test.Udp.Packets.Clear();
        Transport.OnServerReceivedData(client, new byte[] { 2, 1, 2 });
        Transport.OnServerReceivedData(host, HostData(client, 3, 4));
        Assert.Empty(test.Udp.Packets);
        if (timeout) test.Clock.FireAll();
        else Control(client, false, token, "failed");
        Assert.Equal(new byte[] { 7, 3 }, test.PacketsFor(client).Select(packet => packet[0]));
        Assert.Equal(new byte[] { 7, 0 }, test.PacketsFor(host).Select(packet => packet[0]));
        Assert.All(test.Udp.Packets.Where(packet => packet.Data[0] == 7), packet =>
            Assert.False((bool)Json(packet.Data)["direct"]!));
        test.Udp.Packets.Clear();
        Control(host, true, token, "ready");
        test.Clock.FireAll();
        Assert.Empty(test.Udp.Packets);
        Transport.OnServerReceivedData(client, new byte[] { 4, 10, 20 });
        var packet = Assert.Single(test.Udp.Packets);
        Assert.Equal(host.connId, packet.Connection);
        Assert.Equal((byte)4, packet.Method);
        Assert.Equal(new byte[] { 10, 20 }, packet.Data[5..]);
        test.Udp.Packets.Clear();
        Transport.OnServerReceivedData(host, HostData(client, 30, 40));
        Assert.Equal(new byte[] { 30, 40 }, Assert.Single(test.Udp.Packets).Data);
    }

    [Theory]
    [InlineData(false, true, 3, 3)]
    [InlineData(true, false, 3, 3)]
    [InlineData(true, true, 1, 3)]
    [InlineData(true, true, 3, 1)]
    public async Task MissingCapabilityOrLegacyBackendRetainsImmediateAuthentication(bool hostCapable, bool clientCapable, int hostBackend, int clientBackend)
    {
        await using var test = await Harness.StartAsync();
        test.Room.Authenticate(test.HostSecret, backend: hostBackend, webRtcP2P: hostCapable);
        test.Room.Authenticate(test.Room.CurrentRoom().clientSecret!, backend: clientBackend, webRtcP2P: clientCapable);
        Assert.DoesNotContain(test.Udp.Packets.Concat(test.Room.Udp.Packets), packet => packet.Data[0] == 7);
        Assert.Empty(test.Clock.Timers);
    }

    [Fact]
    public async Task NativeNatTakesPriorityAndLateHostNeverIntroducesAuthenticatedClient()
    {
        await using (var test = await Harness.StartAsync())
        {
            test.Room.Authenticate(test.HostSecret, backend: 2, nat: true, webRtcP2P: true);
            test.Room.Authenticate(test.Room.CurrentRoom().clientSecret!, backend: 2, nat: true, webRtcP2P: true);
            Assert.Equal(2, test.Udp.Packets.Count(packet => packet.Data[0] == 6));
            Assert.DoesNotContain(test.Udp.Packets, packet => packet.Data[0] == 7);
        }
        await using (var test = await Harness.StartAsync())
        {
            test.Room.Authenticate(test.Room.CurrentRoom().clientSecret!, backend: 3, webRtcP2P: true);
            test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
            Assert.DoesNotContain(test.Udp.Packets, packet => packet.Data[0] == 7);
        }
    }

    [Fact]
    public async Task TokenCannotSignalOtherPairsOrOtherRooms()
    {
        await using var test = await Harness.StartAsync();
        var host = test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
        var client = test.AddClient();
        var otherClient = test.AddClient();
        var token = test.Token(client);
        await using var otherRoom = await RoomTestHost.StartAsync();
        var otherSecret = await Lobby.CreateRoom(otherRoom.Region, otherRoom.Name);
        var outsider = otherRoom.Authenticate(otherSecret, backend: 3, webRtcP2P: true);
        test.Udp.Packets.Clear();
        Control(otherClient, false, token, "signal", "wrong pair");
        Control(outsider, true, token, "signal", "wrong room");
        Control(host, true, new string('0', 32), "signal", "unknown token");
        Assert.Empty(test.Udp.Packets);
        Control(client, false, token, "signal", "valid answer");
        Assert.Equal(host.connId, Assert.Single(test.Udp.Packets).Connection);
    }

    [Fact]
    public async Task BoundsAllow64IceCandidatesButRejectOversizedOrExcessiveSignaling()
    {
        await using var test = await Harness.StartAsync();
        var host = test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
        var client = test.AddClient();
        var token = test.Token(client);
        test.Udp.Packets.Clear();
        Control(host, true, token, "signal", new string('s', Transport.MaxWebRtcP2PSignalBytes));
        for (int i = 0; i < 64; i++) Control(host, true, token, "signal", "candidate");
        Assert.Equal(65, test.Udp.Packets.Count);
        Assert.All(test.Udp.Packets, packet => Assert.Equal("signal", (string?)Json(packet.Data)["type"]));
        test.Udp.Packets.Clear();
        Control(host, true, token, "signal", new string('s', Transport.MaxWebRtcP2PSignalBytes + 1));
        Assert.Equal("commit", (string?)Json(test.PacketsFor(client)[0])["type"]);
        Assert.False((bool)Json(test.PacketsFor(client)[0])["direct"]!);

        var second = test.AddClient();
        var secondToken = test.Token(second);
        test.Udp.Packets.Clear();
        for (int i = 0; i < 97; i++) Control(second, false, secondToken, "signal", "candidate");
        Assert.Contains(test.PacketsFor(second), packet => packet[0] == 3);
        Assert.Equal(96, test.Udp.Packets.Count(packet => packet.Data[0] == 7 && (string?)Json(packet.Data)["type"] == "signal"));
    }

    [Fact]
    public async Task InvalidControlCannotEscapeParserOrReachGameHandlers()
    {
        await using var test = await Harness.StartAsync();
        var host = test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
        var client = test.AddClient();
        var token = test.Token(client);
        test.Udp.Packets.Clear();
        foreach (var invalid in new[]
        {
            "[]", "{bad", "{\"type\":3,\"token\":null}",
            new string('x', Transport.MaxWebRtcP2PJsonBytes + 1),
            "{\"type\":\"signal\",\"token\":\"" + token + "\",\"signal\":{}}",
            new string('[', 9) + new string(']', 9)
        })
        {
            var bytes = Encoding.UTF8.GetBytes(invalid);
            var packet = new byte[bytes.Length + 1];
            packet[0] = 255;
            bytes.CopyTo(packet, 1);
            Transport.OnServerReceivedData(client, packet);
        }
        Assert.Empty(test.Udp.Packets);
        Control(host, true, token, "ready");
        Control(client, false, token, "ready");
        Assert.True((bool)Json(test.PacketsFor(client)[0])["direct"]!);
    }

    [Fact]
    public async Task ByteBudgetEndsAttemptBeforeSignalingCanGrowWithoutBound()
    {
        await using var test = await Harness.StartAsync();
        var host = test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
        var client = test.AddClient();
        var token = test.Token(client);
        test.Udp.Packets.Clear();
        for (int i = 0; i < 8; i++) Control(host, true, token, "signal", new string('s', Transport.MaxWebRtcP2PSignalBytes));
        Assert.Equal(7, test.Udp.Packets.Count(packet => packet.Data[0] == 7 && (string?)Json(packet.Data)["type"] == "signal"));
        Assert.Contains(test.PacketsFor(client), packet => packet[0] == 3);
        Assert.False((bool)Json(test.PacketsFor(client)[^2])["direct"]!);
    }

    [Fact]
    public async Task PendingPeerLimitFallsBackWithoutChangingOtherAttempts()
    {
        await using var test = await Harness.StartAsync();
        test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
        for (int i = 0; i < Transport.MaxWebRtcP2PPendingPerHost; i++) test.AddClient();
        var overflow = test.Room.Authenticate(test.Room.CurrentRoom().clientSecret!, backend: 3, webRtcP2P: true);
        Assert.Equal(new byte[] { 3 }, test.PacketsFor(overflow).Select(packet => packet[0]));
        Assert.Equal(Transport.MaxWebRtcP2PPendingPerHost, test.Clock.Timers.Count);
    }

    [Fact]
    public async Task DisconnectCancelsTimerWithoutAuthenticatingDepartedClient()
    {
        await using var test = await Harness.StartAsync();
        var host = test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
        var client = test.AddClient();
        var token = test.Token(client);
        test.Udp.Packets.Clear();
        Transport.OnClientLeft(client);
        Assert.Empty(test.PacketsFor(client));
        Assert.Equal(new byte[] { 7, 1 }, test.PacketsFor(host).Select(packet => packet[0]));
        test.Udp.Packets.Clear();
        Control(host, true, token, "ready");
        test.Clock.FireAll();
        Assert.Empty(test.Udp.Packets);
    }

    [Fact]
    public async Task HostMigrationFinishesPendingRelayAttemptsAndDisconnectsCommittedDirectClients()
    {
        await using var test = await Harness.StartAsync();
        var host = test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
        var direct = test.AddClient();
        var directToken = test.Token(direct);
        Control(host, true, directToken, "ready");
        Control(direct, false, directToken, "ready");
        var pending = test.AddClient();
        test.Udp.Packets.Clear();
        Transport.ReleaseRoomHostForMigration(test.Room.CurrentRoom().roomId);
        Assert.Equal(new byte[] { 7, 3 }, test.PacketsFor(pending).Select(packet => packet[0]));
        Assert.False((bool)Json(test.PacketsFor(pending)[0])["direct"]!);
        Assert.Contains(host.connId, test.Udp.Kicked);
        Assert.Contains(direct.connId, test.Udp.Kicked);
        Assert.DoesNotContain(pending.connId, test.Udp.Kicked);
        test.Udp.Packets.Clear();
        test.Clock.FireAll();
        Assert.Empty(test.Udp.Packets);
        var replacement = test.Room.Authenticate(test.HostSecret, backend: 3, webRtcP2P: true);
        Assert.DoesNotContain(test.Udp.Packets, packet => packet.Data[0] == 7);
        var connected = test.PacketsFor(replacement).Single(packet => packet[0] == 0);
        Assert.Equal(5, connected.Length);
        Assert.Equal(pending.connId, BinaryPrimitives.ReadInt32LittleEndian(connected.AsSpan(1)));
    }

    static JObject Json(byte[] packet) => JObject.Parse(Encoding.UTF8.GetString(packet, 1, packet.Length - 1));

    static void Control(PlayerInfo peer, bool host, string token, string type, string? signal = null)
    {
        var json = Encoding.UTF8.GetBytes(new JObject { ["type"] = type, ["token"] = token, ["signal"] = signal }.ToString());
        var packet = new byte[json.Length + 1];
        packet[0] = host ? (byte)3 : (byte)255;
        json.CopyTo(packet, 1);
        Transport.OnServerReceivedData(peer, packet);
    }

    static byte[] HostData(PlayerInfo client, byte a, byte b)
    {
        var packet = new byte[] { 1, 0, 0, 0, 0, 2, a, b };
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1), client.connId);
        return packet;
    }

    sealed class Harness : IAsyncDisposable
    {
        readonly IUdpServer? _oldRtc = HTTPRestAPI.webRtcServer;
        readonly IUdpServer? _oldNative = HTTPRestAPI.udpServerV2;
        readonly TimeProvider _oldClock = Transport.WebRtcP2PClock;
        public RoomTestHost Room { get; private set; } = null!;
        public string HostSecret { get; private set; } = null!;
        public RoomTestHost.RecordingUdpServer Udp { get; } = new();
        public ManualClock Clock { get; } = new();
        public static async Task<Harness> StartAsync()
        {
            var test = new Harness();
            HTTPRestAPI.webRtcServer = HTTPRestAPI.udpServerV2 = test.Udp;
            Transport.WebRtcP2PClock = test.Clock;
            test.Room = await RoomTestHost.StartAsync();
            test.HostSecret = await Lobby.CreateRoom(test.Room.Region, test.Room.Name);
            return test;
        }
        public PlayerInfo AddClient(int backend = 3) => Room.Authenticate(Room.CurrentRoom().clientSecret!,
            backend: backend, webRtcP2P: true, awaitingWebRtcP2P: true);
        public string Token(PlayerInfo client) => (string)Json(PacketsFor(client).Single(packet => packet[0] == 7))["token"]!;
        public byte[][] PacketsFor(PlayerInfo peer) => Udp.Packets.Where(packet => packet.Connection == peer.connId).Select(packet => packet.Data).ToArray();
        public async ValueTask DisposeAsync()
        {
            try { await Room.DisposeAsync(); }
            finally
            {
                HTTPRestAPI.webRtcServer = _oldRtc;
                HTTPRestAPI.udpServerV2 = _oldNative;
                Transport.WebRtcP2PClock = _oldClock;
            }
        }
    }

    sealed class ManualClock : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = new();
        public override long GetTimestamp() => 0;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(8), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ManualTimer(callback, state);
            Timers.Add(timer);
            return timer;
        }
        public void FireAll()
        {
            foreach (var timer in Timers.ToArray()) timer.Fire();
        }
    }

    sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        bool _disposed;
        public void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

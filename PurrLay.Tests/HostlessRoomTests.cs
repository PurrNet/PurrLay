using System.Buffers.Binary;
using Xunit;

namespace PurrLay.Tests;

public sealed class HostlessRoomTests
{
    static async Task<(RoomTestHost host, string hostSecret, Room room, PlayerInfo waiting)> HostLeavesAndClientRejoins()
    {
        var host = await RoomTestHost.StartAsync();
        var hostSecret = await Lobby.CreateRoom(host.Region, host.Name);
        var room = host.CurrentRoom();
        var hostPlayer = host.Authenticate(hostSecret);
        var client = host.Authenticate(room.clientSecret!);
        await host.WaitForCountAsync(2);

        Transport.OnClientLeft(hostPlayer);
        Assert.Contains(client.connId, host.Udp.Kicked);
        Transport.OnClientLeft(client);
        await host.WaitForCountAsync(0);

        var waiting = host.Authenticate(room.clientSecret!, awaitingHost: true);
        await host.WaitForCountAsync(1);
        return (host, hostSecret, room, waiting);
    }

    static byte[][] PacketsFor(RoomTestHost host, PlayerInfo peer) =>
        host.Udp.Packets.Where(packet => packet.Connection == peer.connId).Select(packet => packet.Data).ToArray();

    [Fact]
    public async Task ClientJoiningARoomWithoutAHostIsAuthenticatedOnceTheHostReturns()
    {
        var (host, hostSecret, room, waiting) = await HostLeavesAndClientRejoins();
        await using var cleanup = host;

        Transport.OnServerReceivedData(waiting, new byte[] { Transport.RELIABLE_ORDERED, 1, 2 });
        var returned = host.Authenticate(hostSecret);
        await host.WaitForCountAsync(2);

        Assert.Same(room, host.CurrentRoom());
        Assert.Equal(new byte[] { (byte)SERVER_PACKET_TYPE.SERVER_AUTHENTICATED },
            PacketsFor(host, waiting).Select(packet => packet[0]));
        var hostPackets = PacketsFor(host, returned);
        Assert.Equal(new byte[] { (byte)SERVER_PACKET_TYPE.SERVER_AUTHENTICATED, (byte)SERVER_PACKET_TYPE.SERVER_CLIENT_CONNECTED },
            hostPackets.Select(packet => packet[0]));
        Assert.Equal(waiting.connId, BinaryPrimitives.ReadInt32LittleEndian(hostPackets[1].AsSpan(1)));

        host.Udp.Packets.Clear();
        Transport.OnServerReceivedData(waiting, new byte[] { Transport.RELIABLE_ORDERED, 10, 20 });
        var forwarded = Assert.Single(host.Udp.Packets);
        Assert.Equal(returned.connId, forwarded.Connection);
        Assert.Equal((byte)SERVER_PACKET_TYPE.SERVER_CLIENT_DATA, forwarded.Data[0]);
        Assert.Equal(new byte[] { 10, 20 }, forwarded.Data[5..]);
    }

    [Fact]
    public async Task HostReconnectingWithItsSecretReplacesItsStaleLinkAndKicksItsClients()
    {
        await using var host = await RoomTestHost.StartAsync();
        var hostSecret = await Lobby.CreateRoom(host.Region, host.Name);
        var room = host.CurrentRoom();
        var staleHost = host.Authenticate(hostSecret);
        var client = host.Authenticate(room.clientSecret!);
        await host.WaitForCountAsync(2);

        var reconnected = host.Authenticate(hostSecret);
        await host.WaitForCountAsync(1);
        Assert.Same(room, host.CurrentRoom());
        Assert.Contains(staleHost.connId, host.Udp.Kicked);
        Assert.Contains(client.connId, host.Udp.Kicked);

        Transport.OnClientLeft(staleHost);
        Transport.OnClientLeft(client);
        Assert.Equal(new byte[] { (byte)SERVER_PACKET_TYPE.SERVER_AUTHENTICATED },
            PacketsFor(host, reconnected).Select(packet => packet[0]));

        var rejoined = host.Authenticate(room.clientSecret!);
        await host.WaitForCountAsync(2);
        var connected = PacketsFor(host, reconnected).Single(packet => packet[0] == (byte)SERVER_PACKET_TYPE.SERVER_CLIENT_CONNECTED);
        Assert.Equal(rejoined.connId, BinaryPrimitives.ReadInt32LittleEndian(connected.AsSpan(1)));
    }

    [Fact]
    public async Task RecreatingARoomWithoutAHostKicksTheClientsWaitingInIt()
    {
        var (host, _, room, waiting) = await HostLeavesAndClientRejoins();
        await using var cleanup = host;

        var newSecret = await Lobby.CreateRoom(host.Region, host.Name);
        var replacement = host.CurrentRoom();

        Assert.NotEqual(room.roomId, replacement.roomId);
        Assert.Contains(waiting.connId, host.Udp.Kicked);
        Assert.Empty(PacketsFor(host, waiting));
        host.Authenticate(room.clientSecret!, success: false);
        host.Authenticate(newSecret);
        host.Authenticate(replacement.clientSecret!);
        await host.WaitForCountAsync(2);
    }

    [Fact]
    public async Task RoomWithoutAHostExpiresAndKicksTheClientsWaitingInIt()
    {
        var (host, _, room, waiting) = await HostLeavesAndClientRejoins();
        await using var cleanup = host;

        RoomTestHost.Cleanup(typeof(Lobby), DateTime.UtcNow, TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(waiting.connId, host.Udp.Kicked);
        Assert.Same(room, host.CurrentRoom());

        RoomTestHost.Cleanup(typeof(Lobby), DateTime.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(5));
        Assert.Contains(waiting.connId, host.Udp.Kicked);
        Assert.Empty(PacketsFor(host, waiting));
        await RoomTestHost.WaitUntilAsync(() => !host.HasRelayRoom());
        Assert.False(Transport.TryGetRoomPlayerCount(room.roomId, out _));
    }
}

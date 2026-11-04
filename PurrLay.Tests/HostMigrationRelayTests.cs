extern alias balancer;

using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace PurrLay.Tests;

public sealed class HostMigrationRelayTests
{
    static TestHttpRequest Migration(RoomTestHost host, string path, params (string Name, string? Value)[] headers)
    {
        var all = new List<(string Name, string? Value)> { ("name", host.Name) };
        all.AddRange(headers);
        return new TestHttpRequest(path, all.ToArray());
    }

    static async Task<(HttpStatusCode status, JObject body)> SendRaw(TestHttpRequest request)
    {
        var response = await balancer::PurrBalancer.HTTPRestAPI.OnRequest(request);
        return (response.status, JObject.Parse(Encoding.UTF8.GetString(response.data)));
    }

    [Fact]
    public async Task ClientSecretCannotTakeARoomWhoseHostIsStillConnected()
    {
        await using var host = await RoomTestHost.StartAsync();
        var hostSecret = await Lobby.CreateRoom(host.Region, host.Name);
        var room = host.CurrentRoom();
        var hostPlayer = host.Authenticate(hostSecret);
        host.Authenticate(room.clientSecret!);
        await host.WaitForCountAsync(2);

        var rejected = Assert.Throws<MigrationClaimRejectedException>(() =>
            Lobby.ClaimMigration(host.Name, room.clientSecret!, "intruder", null));
        Assert.Equal("host_connected", rejected.Code);
        Assert.Empty(host.Udp.Kicked);
        Assert.Equal(0, host.CurrentRoom().migrationGeneration);

        // Through the balancer the refusal keeps its code, so a client can tell it apart from other failures.
        var (status, body) = await SendRaw(Migration(host, "/migration/claim",
            ("migration_secret", room.clientSecret), ("promoted_player_id", "intruder")));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("host_connected", (string?)body["code"]);

        // Once the host is gone the client secret is enough: that is a lost host.
        Transport.OnClientLeft(hostPlayer);
        var claim = await host.SendAsync(Migration(host, "/migration/claim",
            ("migration_secret", room.clientSecret), ("promoted_player_id", "survivor"), ("expected_generation", "0")));
        Assert.Equal(1, (int?)claim["generation"]);
        Assert.Equal("survivor", (string?)claim["promotedPlayerId"]);
    }

    [Fact]
    public async Task HostSecretCanStillClaimWhileTheHostIsConnected()
    {
        await using var host = await RoomTestHost.StartAsync();
        var hostSecret = await Lobby.CreateRoom(host.Region, host.Name);
        var hostPlayer = host.Authenticate(hostSecret);
        await host.WaitForCountAsync(1);

        var migration = Lobby.ClaimMigration(host.Name, hostSecret, "next-host", null);
        Assert.Equal(1, migration.generation);
        Assert.Contains(hostPlayer.connId, host.Udp.Kicked);
    }

    [Fact]
    public async Task HandoffAuthorizationIsBoundToItsPlayerExpiresAndIsUsedOnce()
    {
        await using var host = await RoomTestHost.StartAsync();
        var hostSecret = await Lobby.CreateRoom(host.Region, host.Name);
        var room = host.CurrentRoom();
        host.Authenticate(hostSecret);
        host.Authenticate(room.clientSecret!);
        await host.WaitForCountAsync(2);

        // Only the host can authorize.
        var (refused, refusal) = await SendRaw(Migration(host, "/migration/authorize",
            ("host_secret", room.clientSecret), ("promoted_player_id", "2")));
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Equal("invalid_host_secret", (string?)refusal["code"]);

        var authorized = await host.SendAsync(Migration(host, "/migration/authorize",
            ("host_secret", hostSecret), ("promoted_player_id", "2")));
        var token = (string)authorized["authorization"]!;
        Assert.Equal(0, (int?)authorized["generation"]);
        Assert.True((int?)authorized["expiresInSeconds"] > 0);

        Assert.Equal("invalid_secret",
            Assert.Throws<MigrationClaimRejectedException>(() => Lobby.ClaimMigration(host.Name, token, "3", null)).Code);

        var claim = await host.SendAsync(Migration(host, "/migration/claim",
            ("migration_secret", token), ("promoted_player_id", "2"), ("expected_generation", "0")));
        Assert.Equal(1, (int?)claim["generation"]);

        Assert.Equal("invalid_secret",
            Assert.Throws<MigrationClaimRejectedException>(() => Lobby.ClaimMigration(host.Name, token, "2", null)).Code);

        // An expired authorization no longer works.
        var current = host.CurrentRoom();
        var newHost = host.Authenticate((string)claim["secret"]!);
        var (late, _) = Lobby.AuthorizeMigration(host.Name, current.hostSecret!, "4");
        lock (RoomTestHost.ReadState<object>(typeof(Lobby), "_roomLock"))
            current.migrationAuthorizationExpires = DateTime.UtcNow.AddSeconds(-1);
        Assert.Equal("invalid_secret",
            Assert.Throws<MigrationClaimRejectedException>(() => Lobby.ClaimMigration(host.Name, late, "4", null)).Code);
        Assert.DoesNotContain(newHost.connId, host.Udp.Kicked);
    }

    [Fact]
    public async Task JoinDetailsCarryTheMigrationGeneration()
    {
        await using var host = await RoomTestHost.StartAsync();
        var hostSecret = await Lobby.CreateRoom(host.Region, host.Name);
        Assert.Equal(0, (int?)(await host.JoinAsync())["migrationGeneration"]);

        Lobby.ClaimMigration(host.Name, hostSecret, "next-host", 0);
        var joined = await host.JoinAsync();
        Assert.Equal(1, (int?)joined["migrationGeneration"]);
        Assert.Equal(host.CurrentRoom().clientSecret, (string?)joined["secret"]);
    }

    [Fact]
    public async Task AGenerationMismatchIsRefusedWithItsCode()
    {
        await using var host = await RoomTestHost.StartAsync();
        var hostSecret = await Lobby.CreateRoom(host.Region, host.Name);

        var (status, body) = await SendRaw(Migration(host, "/migration/claim",
            ("migration_secret", hostSecret), ("expected_generation", "5")));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("generation_mismatch", (string?)body["code"]);
        Assert.Equal(0, (int?)body["generation"]);
        Assert.Equal(0, host.CurrentRoom().migrationGeneration);
    }
}

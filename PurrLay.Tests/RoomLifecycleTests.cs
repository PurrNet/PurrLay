extern alias balancer;

using Xunit;
using BalancerApi = balancer::PurrBalancer.HTTPRestAPI;

namespace PurrLay.Tests;

public sealed class RoomLifecycleTests
{
    [Fact]
    public async Task ReusingGhostRoomRestoresRouteAndAuthenticatesNewHostAndClient()
    {
        await using var host = await RoomTestHost.StartAsync();
        var oldHostSecret = await Lobby.CreateRoom(host.Region, host.Name);
        var oldRoom = host.CurrentRoom();
        var oldRegistration = Assert.Single(host.Registrations);
        var oldInstanceId = oldRegistration.Headers["room_instance_id"];
        Assert.True(Guid.TryParse(oldInstanceId, out _));
        var oldHost = host.Authenticate(oldHostSecret);
        var oldClient = host.Authenticate(oldRoom.clientSecret!);
        await host.WaitForCountAsync(2);
        Transport.OnClientLeft(oldHost);
        await host.WaitForCountAsync(0);
        Assert.Same(oldRoom, host.CurrentRoom());

        // Reproduce the reported desync: the relay retains an empty room after
        // the balancer's route has disappeared, then the same name is reused.
        await host.SendAsync(host.RoomRequest("/unregisterRoom", oldInstanceId));
        await Assert.ThrowsAsync<Exception>(() => host.JoinAsync());

        var newHostSecret = await Lobby.CreateRoom(host.Region, host.Name);
        var replacement = host.CurrentRoom();
        Assert.NotEqual(oldRoom.roomId, replacement.roomId);
        Assert.NotEqual(oldHostSecret, newHostSecret);
        Assert.NotEqual(oldRoom.clientSecret, replacement.clientSecret);
        Assert.Equal(replacement.clientSecret, (string?)(await host.JoinAsync())["secret"]);

        host.Authenticate(oldHostSecret, success: false);
        host.Authenticate(oldRoom.clientSecret!, success: false);
        host.Authenticate(newHostSecret);
        host.Authenticate(replacement.clientSecret!);
        await host.WaitForCountAsync(2);

        // Queued callbacks for the previous transport room cannot affect the new one.
        Lobby.UpdateRoomPlayerCount(oldRoom.roomId, 0);
        Lobby.RemoveRoom(oldRoom.roomId);
        Transport.OnClientLeft(oldClient);
        Assert.Same(replacement, host.CurrentRoom());
        Assert.True(Transport.TryGetRoomPlayerCount(replacement.roomId, out var count));
        Assert.Equal(2, count);
        Assert.Equal(replacement.clientSecret, (string?)(await host.JoinAsync())["secret"]);
    }

    [Fact]
    public async Task ReusingEmptyRegisteredRoomReplacesTheExistingBalancerGeneration()
    {
        await using var host = await RoomTestHost.StartAsync();
        await Lobby.CreateRoom(host.Region, host.Name);
        var original = host.CurrentRoom();
        await Lobby.CreateRoom(host.Region, host.Name);
        var replacement = host.CurrentRoom();
        var registrations = host.Registrations.ToArray();

        Assert.Equal(2, registrations.Length);
        Assert.NotEqual(registrations[0].Headers["room_instance_id"], registrations[1].Headers["room_instance_id"]);
        Assert.Equal(registrations[0].Headers["room_instance_id"], registrations[1].Headers["previous_room_instance_id"]);
        Assert.NotEqual(original.roomId, replacement.roomId);
        Assert.Equal(replacement.clientSecret, (string?)(await host.JoinAsync())["secret"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRegistrationRemainsUnjoinableAndCanRetryTheSameInstance(bool committed)
    {
        await using var host = await RoomTestHost.StartAsync();
        host.FailFirstRegistration = true;
        host.FailAfterRegistrationCommit = committed;

        await Assert.ThrowsAsync<Exception>(() => Lobby.CreateRoom(host.Region, host.Name));
        Assert.False(Lobby.TryGetRoom(host.Name, out _));
        Assert.False(Lobby.TryGetMigrationState(host.Name, out _));
        await Assert.ThrowsAsync<Exception>(() => host.JoinAsync());

        var secret = await Lobby.CreateRoom(host.Region, host.Name);
        var registrations = host.Registrations.ToArray();
        Assert.Equal(2, registrations.Length);
        Assert.Equal(registrations[0].Headers["room_instance_id"], registrations[1].Headers["room_instance_id"]);
        Assert.Equal(secret, host.CurrentRoom().hostSecret);
        Assert.Equal(host.CurrentRoom().clientSecret, (string?)(await host.JoinAsync())["secret"]);
        host.Authenticate(secret);
        await host.WaitForCountAsync(1);
    }

    [Fact]
    public async Task ConcurrentCreationIsRejectedUntilRegistrationCompletes()
    {
        await using var host = await RoomTestHost.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeRegistration = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var creation = Lobby.CreateRoom(host.Region, host.Name);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<Exception>(() => Lobby.CreateRoom(host.Region, host.Name));
            Assert.False(Lobby.TryGetRoom(host.Name, out _));
            Assert.Single(host.Registrations);
        }
        finally
        {
            release.TrySetResult();
        }

        var secret = await creation;
        Assert.Equal(secret, host.CurrentRoom().hostSecret);
        host.Authenticate(secret);
        await host.WaitForCountAsync(1);
        await Assert.ThrowsAsync<Exception>(() => Lobby.CreateRoom(host.Region, host.Name));
        Assert.Single(host.Registrations);
    }

    [Fact]
    public async Task ReusingAnExpiredRelayRoomRefreshesItsCleanupDeadline()
    {
        await using var host = await RoomTestHost.StartAsync();
        await Lobby.CreateRoom(host.Region, host.Name);
        var expired = host.CurrentRoom();
        expired.emptySince = DateTime.UtcNow.AddMinutes(-10);

        await Lobby.CreateRoom(host.Region, host.Name);
        var replacement = host.CurrentRoom();
        RoomTestHost.Cleanup(typeof(Lobby), DateTime.UtcNow, TimeSpan.FromMinutes(5));

        Assert.Same(replacement, host.CurrentRoom());
        Assert.NotEqual(expired.roomId, replacement.roomId);
        Assert.Equal(replacement.clientSecret, (string?)(await host.JoinAsync())["secret"]);
    }

    [Fact]
    public async Task BalancerDoesNotIndependentlyExpireVersionedRelayRooms()
    {
        await using var host = await RoomTestHost.StartAsync();
        await Lobby.CreateRoom(host.Region, host.Name);

        // An aged balancer clock must not invalidate a room the relay still owns.
        RoomTestHost.Cleanup(typeof(BalancerApi), DateTime.UtcNow.AddDays(1), TimeSpan.FromSeconds(1));

        Assert.NotNull(await host.ListedRoomAsync());
        Assert.Equal(host.CurrentRoom().clientSecret, (string?)(await host.JoinAsync())["secret"]);
    }

    [Fact]
    public async Task RecreationWhileOldUnregisterIsDelayedSurvivesItsEventualCompletion()
    {
        await using var host = await RoomTestHost.StartAsync();
        await Lobby.CreateRoom(host.Region, host.Name);
        var original = host.CurrentRoom();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeUnregistration = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        Lobby.RemoveRoom(original.roomId);
        Room replacement;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Lobby.TryGetRoom(host.Name, out _));
            Assert.NotNull(await host.ListedRoomAsync());
            await Lobby.CreateRoom(host.Region, host.Name);
            replacement = host.CurrentRoom();
            Assert.NotEqual(original.roomId, replacement.roomId);
            Assert.Equal(replacement.clientSecret, (string?)(await host.JoinAsync())["secret"]);
        }
        finally
        {
            release.TrySetResult();
        }

        await RoomTestHost.WaitUntilAsync(() => !RoomTestHost.ReadRoomState<bool>(original, "removalInProgress"));
        Assert.Same(replacement, host.CurrentRoom());
        Assert.Equal(replacement.clientSecret, (string?)(await host.JoinAsync())["secret"]);
        host.Authenticate(replacement.hostSecret!);
        await host.WaitForCountAsync(1);
    }

    [Fact]
    public async Task FailedUnregisterAfterSoleHostLeavesIsRetriedByCleanup()
    {
        await using var host = await RoomTestHost.StartAsync();
        var secret = await Lobby.CreateRoom(host.Region, host.Name);
        var original = host.CurrentRoom();
        var player = host.Authenticate(secret);
        await host.WaitForCountAsync(1);
        Assert.Null(original.emptySince);
        host.FailFirstUnregistration = true;

        Transport.OnClientLeft(player);
        await RoomTestHost.WaitUntilAsync(() => host.Unregistrations.Count == 1 &&
            !RoomTestHost.ReadRoomState<bool>(original, "removalInProgress"));
        Assert.False(Lobby.TryGetRoom(host.Name, out _));
        Assert.NotNull(await host.ListedRoomAsync());

        RoomTestHost.Cleanup(typeof(Lobby), DateTime.UtcNow, TimeSpan.FromMinutes(5));
        await RoomTestHost.WaitUntilAsync(() => !host.HasRelayRoom());
        Assert.Equal(2, host.Unregistrations.Count);
        Assert.Null(await host.ListedRoomAsync());
        await Lobby.CreateRoom(host.Region, host.Name);
        Assert.Equal(host.CurrentRoom().clientSecret, (string?)(await host.JoinAsync())["secret"]);
    }

    [Fact]
    public async Task MigrationClaimRotatesSecretsAndAllowsANewHostWithoutLosingClients()
    {
        await using var host = await RoomTestHost.StartAsync();
        var originalSecret = await Lobby.CreateRoom(host.Region, host.Name);
        var original = host.CurrentRoom();
        var originalClientSecret = original.clientSecret!;
        var oldHost = host.Authenticate(originalSecret);
        host.Authenticate(originalClientSecret);
        await host.WaitForCountAsync(2);

        var migration = Lobby.ClaimMigration(host.Name, originalClientSecret, "promoted-player", 0);
        Assert.Equal(original.roomId, migration.roomId);
        Assert.Equal(1, migration.generation);
        Assert.NotNull(migration.fencingToken);
        Assert.Equal("promoted-player", migration.promotedPlayerId);
        Assert.NotEqual(originalSecret, migration.hostSecret);
        Assert.NotEqual(originalClientSecret, migration.clientSecret);
        await host.WaitForCountAsync(1);

        host.Authenticate(originalSecret, success: false);
        host.Authenticate(migration.hostSecret!);
        Transport.OnClientLeft(oldHost);
        await host.WaitForCountAsync(2);
        Assert.Throws<Exception>(() => Lobby.ClaimMigration(host.Name, migration.clientSecret!, "another-player", 0));
        Assert.Equal(migration.clientSecret, (string?)(await host.JoinAsync())["secret"]);
    }
}

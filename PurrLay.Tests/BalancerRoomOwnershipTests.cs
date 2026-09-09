extern alias balancer;

using System.Net;
using Xunit;
using BalancerApi = balancer::PurrBalancer.HTTPRestAPI;

namespace PurrLay.Tests;

public sealed class BalancerRoomOwnershipTests
{
    [Fact]
    public async Task DelayedOldUnregisterAndCountCannotChangeAReplacementRoom()
    {
        await using var host = await RoomTestHost.StartAsync();
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await host.SendAsync(host.RoomRequest("/registerRoom", first));
        await host.SendAsync(host.RoomRequest("/registerRoom", second, first));
        await host.SendAsync(host.RoomRequest("/updateConnectionCount", second, count: 3, sequence: 1));

        await host.SendAsync(host.RoomRequest("/updateConnectionCount", first, count: 0, sequence: long.MaxValue));
        await host.SendAsync(host.RoomRequest("/unregisterRoom", first));

        Assert.Equal(3, (int?)(await host.ListedRoomAsync())?["connectedPlayers"]);
        Assert.Equal(second, RoomTestHost.ReadState<Dictionary<string, string>>(typeof(BalancerApi), "_roomInstanceIds")[host.Name]);
        await host.SendAsync(host.RoomRequest("/unregisterRoom", second));
        Assert.Null(await host.ListedRoomAsync());
        await host.SendAsync(host.RoomRequest("/unregisterRoom", second));
    }

    [Fact]
    public async Task OutOfOrderCountsAndRegistrationRetryPreserveTheLatestCount()
    {
        await using var host = await RoomTestHost.StartAsync();
        var id = Guid.NewGuid().ToString();
        await host.SendAsync(host.RoomRequest("/registerRoom", id));
        await host.SendAsync(host.RoomRequest("/updateConnectionCount", id, count: 0, sequence: 2));
        await host.SendAsync(host.RoomRequest("/updateConnectionCount", id, count: 4, sequence: 1));
        await host.SendAsync(host.RoomRequest("/registerRoom", id));
        await host.SendAsync(host.RoomRequest("/updateConnectionCount", id, count: 5, sequence: 2));
        Assert.Equal(0, (int?)(await host.ListedRoomAsync())?["connectedPlayers"]);

        await host.SendAsync(host.RoomRequest("/updateConnectionCount", id, count: 2, sequence: 3));
        Assert.Equal(2, (int?)(await host.ListedRoomAsync())?["connectedPlayers"]);
    }

    [Fact]
    public async Task LegacyAndWrongEndpointMutationsCannotChangeVersionedRooms()
    {
        await using var host = await RoomTestHost.StartAsync();
        var id = Guid.NewGuid().ToString();
        await host.SendAsync(host.RoomRequest("/registerRoom", id));
        await host.SendAsync(host.RoomRequest("/updateConnectionCount", id, count: 2, sequence: 1));

        await host.SendAsync(host.RoomRequest("/updateConnectionCount", count: 0));
        await host.SendAsync(host.RoomRequest("/unregisterRoom"));
        await host.SendAsync(host.RoomRequest("/updateConnectionCount", id, count: 0, sequence: 3, endpoint: "http://wrong-relay"));
        await host.SendAsync(host.RoomRequest("/unregisterRoom", id, endpoint: "http://wrong-relay"));

        Assert.Equal(2, (int?)(await host.ListedRoomAsync())?["connectedPlayers"]);
    }

    [Fact]
    public async Task ReplacementRequiresTheCurrentPredecessor()
    {
        await using var host = await RoomTestHost.StartAsync();
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await host.SendAsync(host.RoomRequest("/registerRoom", first));

        var response = await BalancerApi.OnRequest(host.RoomRequest("/registerRoom", second));
        Assert.Equal(HttpStatusCode.Conflict, response.status);
        response = await BalancerApi.OnRequest(host.RoomRequest("/registerRoom", second, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Conflict, response.status);

        await host.SendAsync(host.RoomRequest("/registerRoom", second, first));
        response = await BalancerApi.OnRequest(host.RoomRequest("/registerRoom", first));
        Assert.Equal(HttpStatusCode.Conflict, response.status);
        Assert.NotNull(await host.ListedRoomAsync());
    }

    [Fact]
    public async Task UpgradeCanReplaceEmptyLegacyRoomButCannotReplaceAnOccupiedLegacyRoom()
    {
        await using var host = await RoomTestHost.StartAsync();
        var id = Guid.NewGuid().ToString();
        await host.SendAsync(host.RoomRequest("/registerRoom"));
        await host.SendAsync(host.RoomRequest("/updateConnectionCount", count: 1));

        var response = await BalancerApi.OnRequest(host.RoomRequest("/registerRoom", id));
        Assert.Equal(HttpStatusCode.Conflict, response.status);
        await host.SendAsync(host.RoomRequest("/updateConnectionCount", count: 0));
        await host.SendAsync(host.RoomRequest("/registerRoom", id));
        Assert.NotNull(await host.ListedRoomAsync());
        Assert.False(RoomTestHost.ReadState<Dictionary<string, DateTime>>(typeof(BalancerApi), "_emptyRoomSince").ContainsKey(host.Name));
    }

    [Fact]
    public async Task CleanupExpiresLegacyRoomsOnlyAfterTheirCurrentEmptyDeadline()
    {
        await using var host = await RoomTestHost.StartAsync();
        await host.SendAsync(host.RoomRequest("/registerRoom"));
        var emptySince = RoomTestHost.ReadState<Dictionary<string, DateTime>>(typeof(BalancerApi), "_emptyRoomSince");
        emptySince[host.Name] = DateTime.UtcNow.AddMinutes(-10);

        // A join followed by departure resets the previous empty interval.
        await host.SendAsync(host.RoomRequest("/updateConnectionCount", count: 1));
        await host.SendAsync(host.RoomRequest("/updateConnectionCount", count: 0));
        RoomTestHost.Cleanup(typeof(BalancerApi), DateTime.UtcNow, TimeSpan.FromMinutes(5));
        Assert.NotNull(await host.ListedRoomAsync());

        emptySince[host.Name] = DateTime.UtcNow.AddMinutes(-10);
        RoomTestHost.Cleanup(typeof(BalancerApi), DateTime.UtcNow, TimeSpan.FromMinutes(5));
        Assert.Null(await host.ListedRoomAsync());
    }

    [Fact]
    public async Task RelayHeartbeatPreservesRoomsAndRestartClearsPreviousProcessRoutes()
    {
        await using var host = await RoomTestHost.StartAsync();
        var firstProcess = Guid.NewGuid().ToString("N");
        var secondProcess = Guid.NewGuid().ToString("N");
        await host.RegisterServerAsync(firstProcess);
        var register = host.RoomRequest("/registerRoom", Guid.NewGuid().ToString("N"));
        register.Headers["relay_instance_id"] = firstProcess;
        await host.SendAsync(register);

        await host.RegisterServerAsync(firstProcess);
        Assert.NotNull(await host.ListedRoomAsync());

        await host.RegisterServerAsync(secondProcess);
        Assert.Null(await host.ListedRoomAsync());
        var staleResponse = await BalancerApi.OnRequest(register);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.status);
        Assert.Null(await host.ListedRoomAsync());

        register.Headers["relay_instance_id"] = secondProcess;
        await host.SendAsync(register);
        await host.RegisterServerAsync(firstProcess);
        await host.SendAsync(host.ServerRequest("/unregisterServer", firstProcess));
        Assert.NotNull(await host.ListedRoomAsync());
    }
}

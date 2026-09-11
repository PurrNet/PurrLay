using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using PurrBalancer;
using Xunit;

namespace PurrLay.Tests;

public sealed class RelayDeploymentTests
{
    [Fact]
    public async Task HeartbeatRestoresOnlyTheMatchingActiveGenerationAndCannotUndoAnAdminDrain()
    {
        using var deployment = new DeploymentScope(standby: true);
        var revision = RelayDeployment.AdministrationRevision;
        var response = new JObject
        {
            ["instanceId"] = "previous-process",
            ["deploymentId"] = "deployment-test",
            ["acceptingRooms"] = true
        };
        RelayDeployment.ApplyRegistrationResponse(response, revision);
        Assert.True(RelayDeployment.Draining);
        response["instanceId"] = Program.ProcessInstanceId;
        response["deploymentId"] = "previous-deployment";
        RelayDeployment.ApplyRegistrationResponse(response, revision);
        Assert.True(RelayDeployment.Draining);
        response["deploymentId"] = "deployment-test";
        RelayDeployment.ApplyRegistrationResponse(response, revision);
        Assert.False(RelayDeployment.Draining);
        response["acceptingRooms"] = false;
        RelayDeployment.ApplyRegistrationResponse(response, revision);
        Assert.False(RelayDeployment.Draining);
        response["acceptingRooms"] = true;
        await Admin("drain");
        RelayDeployment.ApplyRegistrationResponse(response, revision);
        RelayDeployment.ApplyRegistrationResponse(response, RelayDeployment.AdministrationRevision);
        Assert.True(RelayDeployment.Draining);
        await Admin("retire");
        RelayDeployment.ApplyRegistrationResponse(response, RelayDeployment.AdministrationRevision);
        Assert.True(RelayDeployment.Retired);
    }

    [Fact]
    public void TransportPortsAreConfigurableWithoutChangingLegacyDefaults()
    {
        var names = new[] { "UDP_PORT", "UDP_PORT_V2", "WEBSOCKETS_PORT" };
        var originals = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            Assert.Equal((7777, 7778, 6942), (Program.UDP_PORT, Program.UDP_PORT_V2, Program.WEBSOCKETS_PORT));
            for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], (24001 + i).ToString());
            Assert.Equal((24001, 24002, 24003), (Program.UDP_PORT, Program.UDP_PORT_V2, Program.WEBSOCKETS_PORT));
            Environment.SetEnvironmentVariable("UDP_PORT", "0");
            Assert.Throws<ArgumentOutOfRangeException>(() => Program.UDP_PORT);
        }
        finally
        {
            for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], originals[i]);
        }
    }

    [Fact]
    public async Task StandbyRequiresReadinessAndAuthenticatedInstanceScopedAdministration()
    {
        using var deployment = new DeploymentScope(standby: true);
        var oldWs = HTTPRestAPI.webServer;
        var oldV1 = HTTPRestAPI.udpServerV1;
        var oldV2 = HTTPRestAPI.udpServerV2;
        var oldRtc = HTTPRestAPI.webRtcRuntime;
        using var ws = new WebSockets(0);
        try
        {
            HTTPRestAPI.webServer = ws;
            HTTPRestAPI.udpServerV1 = new RoomTestHost.RecordingUdpServer();
            HTTPRestAPI.udpServerV2 = null;
            HTTPRestAPI.webRtcRuntime = null;
            Assert.True((bool)RelayDeployment.Status()["draining"]!);
            Assert.False((bool)RelayDeployment.Status()["ready"]!);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Admin("activate")).status);
            Assert.Equal(HttpStatusCode.Forbidden, (await HTTPRestAPI.OnRequest(new TestHttpRequest("/admin/status"))).status);
            Assert.Equal(HttpStatusCode.Conflict, (await Admin("activate", "old-instance")).status);
            Assert.Equal(HttpStatusCode.Conflict, (await Admin("activate", null)).status);

            HTTPRestAPI.udpServerV2 = new RoomTestHost.RecordingUdpServer();
            Assert.Equal(HttpStatusCode.OK, (await Admin("activate")).status);
            Assert.False((bool)RelayDeployment.Status()["draining"]!);
            Assert.Equal("deployment-test", (string?)RelayDeployment.Status()["deploymentId"]);
            Assert.Equal(HttpStatusCode.OK, (await Admin("activate")).status);
            Assert.Equal(HttpStatusCode.OK, (await Admin("drain")).status);
            Assert.Equal(HttpStatusCode.OK, (await Admin("retire")).status);
            Assert.True((bool)RelayDeployment.Status()["retired"]!);
            Assert.Equal(HttpStatusCode.Conflict, (await Admin("activate")).status);
            Assert.Equal(HttpStatusCode.OK, (await HTTPRestAPI.OnRequest(new TestHttpRequest("/ping"))).status);
        }
        finally
        {
            HTTPRestAPI.webServer = oldWs;
            HTTPRestAPI.udpServerV1 = oldV1;
            HTTPRestAPI.udpServerV2 = oldV2;
            HTTPRestAPI.webRtcRuntime = oldRtc;
        }
    }

    [Fact]
    public async Task DrainPreservesPendingAllocationsEmptyRoomsAndLateRoomAuthentication()
    {
        using var deployment = new DeploymentScope();
        await using var host = await RoomTestHost.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeRegistration = async _ => { entered.TrySetResult(); await release.Task; };
        var creation = Lobby.CreateRoom(host.Region, host.Name);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, (await Admin("drain")).status);
            Assert.Equal(1, (int)RelayDeployment.Status()["pendingAllocations"]!);
            Assert.Equal(HttpStatusCode.Conflict, (await Admin("retire")).status);
            await Assert.ThrowsAsync<RelayDrainingException>(() => Lobby.CreateRoom(host.Region, host.Name + "-new"));
        }
        finally { release.TrySetResult(); }

        var secret = await creation;
        Assert.Equal(0, (int)RelayDeployment.Status()["pendingAllocations"]!);
        Assert.Equal(1, (int)RelayDeployment.Status()["roomCount"]!);
        Assert.Equal(0, (int)RelayDeployment.Status()["transportConnections"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin("retire")).status);
        Assert.Equal(HttpStatusCode.OK, (await HTTPRestAPI.OnRequest(new TestHttpRequest("/getJoinDetails", ("name", host.Name)))).status);

        var owner = host.Authenticate(secret);
        var client = host.Authenticate(host.CurrentRoom().clientSecret!);
        Assert.Equal(2, (int)RelayDeployment.Status()["transportConnections"]!);
        Transport.OnClientLeft(client);
        Transport.OnClientLeft(owner);
        await RoomTestHost.WaitUntilAsync(() => !host.HasRelayRoom());
        Assert.Equal(HttpStatusCode.OK, (await Admin("retire")).status);
    }

    [Fact]
    public async Task RetirementWaitsForRawPeersPipePeersAndSignalingThenSealsAdmission()
    {
        using var deployment = new DeploymentScope(standby: true);
        var original = HTTPRestAPI.udpServerV1;
        HTTPRestAPI.udpServerV1 = new RoomTestHost.RecordingUdpServer();
        var peer = new PlayerInfo(HTTPRestAPI.CreateCallbacks(1).ReserveConnId(true), true);
        try
        {
            Assert.Equal(1, (int)RelayDeployment.Status()["transportConnections"]!);
            Assert.Equal(0, (int)RelayDeployment.Status()["pipeConnections"]!);
            Assert.Equal(HttpStatusCode.Conflict, (await Admin("retire")).status);
            Transport.OnServerReceivedData(peer, Encoding.UTF8.GetBytes("{\"pipe\":true}"));
            Assert.Equal(1, (int)RelayDeployment.Status()["pipeConnections"]!);
            Assert.Equal(HttpStatusCode.Conflict, (await Admin("retire")).status);
            Transport.OnClientLeft(peer);

            Assert.True(RelayDeployment.TryBeginOffer());
            try
            {
                Assert.Equal(1, (int)RelayDeployment.Status()["pendingOffers"]!);
                Assert.Equal(HttpStatusCode.Conflict, (await Admin("retire")).status);
            }
            finally { RelayDeployment.EndOffer(); }

            Assert.Equal(HttpStatusCode.OK, (await Admin("retire")).status);
            Assert.Equal(0, Transport.ReserveConnId(false));
            Assert.Equal(0, HTTPRestAPI.CreateCallbacks(2).ReserveConnId(true));
            Assert.Equal(0, HTTPRestAPI.CreateCallbacks(3).ReserveConnId(true));
            Assert.False(RelayDeployment.TryBeginOffer());
            Transport.OnServerReceivedData(peer, Encoding.UTF8.GetBytes("{\"pipe\":true}"));
            Assert.False(PipeRelay.IsClient(peer.connId));
        }
        finally
        {
            Transport.OnClientLeft(peer);
            HTTPRestAPI.udpServerV1 = original;
        }
    }

    [Fact]
    public async Task ConcurrentRetirementAndConnectionReservationCannotAdmitAnUncountedPeer()
    {
        using var deployment = new DeploymentScope();
        for (var i = 0; i < 25; i++)
        {
            RelayDeployment.Initialize("deployment-test", true);
            var admission = Task.Run(() => Transport.ReserveConnId(true));
            var retirement = Task.Run(() => Admin("retire"));
            await Task.WhenAll(admission, retirement);
            var id = await admission;
            try
            {
                Assert.Equal(id == 0 ? HttpStatusCode.OK : HttpStatusCode.Conflict, (await retirement).status);
                Assert.Equal(id == 0 ? 0 : 1, (int)RelayDeployment.Status()["transportConnections"]!);
            }
            finally { if (id != 0) Transport.OnClientLeft(new PlayerInfo(id, true)); }
        }
    }

    [Fact]
    public async Task KickedLegacyWebSocketPeerReleasesItsPipeAndTransportDrainCounts()
    {
        using var deployment = new DeploymentScope();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var original = HTTPRestAPI.webServer;
        using var server = new WebSockets(port);
        HTTPRestAPI.webServer = server;
        using var client = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);
            await client.SendAsync(Encoding.UTF8.GetBytes("{\"pipe\":true}"), WebSocketMessageType.Binary, true, timeout.Token);
            var reply = new byte[32];
            var received = await client.ReceiveAsync(reply.AsMemory(), timeout.Token);
            Assert.Equal(5, received.Count);
            var id = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1));
            await Admin("drain");
            server.KickClient(id);
            await RoomTestHost.WaitUntilAsync(() => Transport.GetTransportConnectionCount() == 0 && !PipeRelay.IsClient(id));
            Assert.Equal(HttpStatusCode.OK, (await Admin("retire")).status);
        }
        finally
        {
            client.Abort();
            await RoomTestHost.WaitUntilAsync(() => Transport.GetTransportConnectionCount() == 0);
            HTTPRestAPI.webServer = original;
        }
    }

    static Task<ApiResponse> Admin(string action) => Admin(action, Program.ProcessInstanceId);
    static Task<ApiResponse> Admin(string action, string? instance) => HTTPRestAPI.OnRequest(new TestHttpRequest(
        "/admin/" + action, ("internal_key_secret", "PURRNET"), ("relay_instance_id", instance))
        { Method = WatsonWebserver.Core.HttpMethod.POST });

    sealed class DeploymentScope : IDisposable
    {
        readonly string? _id = RelayDeployment.DeploymentId;
        readonly bool _draining = RelayDeployment.Draining;
        public DeploymentScope(bool standby = false) => RelayDeployment.Initialize("deployment-test", standby);
        public void Dispose() => RelayDeployment.Initialize(_id, _draining);
    }
}

extern alias balancer;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;
using BalancerApi = balancer::PurrBalancer.HTTPRestAPI;

namespace PurrLay.Tests;

internal sealed class RoomTestHost : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentBag<Task> _requests = new();
    private readonly List<PlayerInfo> _players = new();
    private readonly Dictionary<string, string?> _originalEnvironment = new();
    private readonly WebSockets? _originalWebServer = HTTPRestAPI.webServer;
    private readonly IUdpServer? _originalUdpServer = HTTPRestAPI.udpServerV1;
    private readonly Task _listenTask;
    private int _registrationCount;
    private int _unregistrationCount;

    public string Endpoint { get; }
    public string Region { get; } = "test-" + Guid.NewGuid().ToString("N");
    public string Name { get; } = "reusable-chatroom-" + Guid.NewGuid().ToString("N");
    public RecordingUdpServer Udp { get; } = new();
    public ConcurrentQueue<TestHttpRequest> Registrations { get; } = new();
    public ConcurrentQueue<TestHttpRequest> Unregistrations { get; } = new();
    public Func<TestHttpRequest, Task>? BeforeRegistration { get; set; }
    public Func<TestHttpRequest, Task>? BeforeUnregistration { get; set; }
    public bool FailFirstRegistration { get; set; }
    public bool FailAfterRegistrationCommit { get; set; }
    public bool FailFirstUnregistration { get; set; }

    private RoomTestHost()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        Endpoint = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Endpoint + "/");
        _listener.Start();
        _listenTask = ListenAsync();

        SetEnvironment("BALANCER_URL", Endpoint);
        SetEnvironment("HOST_ENDPOINT", Endpoint);
        SetEnvironment("HOST_SSL", "false");
        HTTPRestAPI.webServer = new WebSockets(0);
        HTTPRestAPI.udpServerV1 = Udp;
    }

    public static async Task<RoomTestHost> StartAsync()
    {
        var host = new RoomTestHost();
        await host.RegisterServerAsync();
        return host;
    }

    public async Task RegisterServerAsync(string? processInstanceId = null)
    {
        await BalancerApi.OnRequest(ServerRequest("/registerServer", processInstanceId));
    }

    public TestHttpRequest ServerRequest(string path, string? processInstanceId = null) =>
        new(path, ("internal_key_secret", "PURRNET"))
        {
            Method = WatsonWebserver.Core.HttpMethod.POST,
            Data = new MemoryStream(Encoding.UTF8.GetBytes(new JObject
            {
                ["apiEndpoint"] = Endpoint,
                ["host"] = "127.0.0.1",
                ["region"] = Region,
                ["instanceId"] = processInstanceId
            }.ToString()))
        };

    private void SetEnvironment(string name, string value)
    {
        _originalEnvironment.Add(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private async Task ListenAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                _requests.Add(HandleAsync(context));
            }
        }
        catch (HttpListenerException) when (!_listener.IsListening) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var req = new TestHttpRequest(context.Request.Url!.AbsolutePath);
            req.Headers.Add(context.Request.Headers);
            var registering = req.Url.RawWithoutQuery == "/registerRoom";
            var fail = registering && Interlocked.Increment(ref _registrationCount) == 1 && FailFirstRegistration;
            if (registering)
            {
                Registrations.Enqueue(req);
                if (BeforeRegistration != null)
                    await BeforeRegistration(req);
                if (fail && !FailAfterRegistrationCommit)
                    throw new IOException("Simulated unavailable balancer");
            }
            if (req.Url.RawWithoutQuery == "/unregisterRoom")
            {
                var attempt = Interlocked.Increment(ref _unregistrationCount);
                Unregistrations.Enqueue(req);
                if (BeforeUnregistration != null)
                    await BeforeUnregistration(req);
                if (attempt == 1 && FailFirstUnregistration)
                    throw new IOException("Simulated failed unregister");
            }

            byte[] data;
            if (req.Url.RawWithoutQuery == "/getJoinDetails")
            {
                var response = await HTTPRestAPI.OnRequest(req);
                data = response.data;
                context.Response.StatusCode = (int)response.status;
            }
            else
            {
                var response = await BalancerApi.OnRequest(req);
                data = response.data;
                context.Response.StatusCode = (int)response.status;
            }

            if (fail)
                throw new IOException("Simulated lost registration response after commit");
            context.Response.ContentLength64 = data.Length;
            await context.Response.OutputStream.WriteAsync(data);
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = 500;
            var data = Encoding.UTF8.GetBytes(exception.Message);
            context.Response.ContentLength64 = data.Length;
            await context.Response.OutputStream.WriteAsync(data);
        }
        finally
        {
            context.Response.Close();
        }
    }

    public TestHttpRequest RoomRequest(string path, string? instanceId = null, string? previousInstanceId = null,
        int? count = null, long? sequence = null, string? endpoint = null) => new(path,
        ("name", Name), ("region", Region), ("internal_key_secret", "PURRNET"),
        ("relay_endpoint", endpoint ?? Endpoint), ("room_instance_id", instanceId),
        ("previous_room_instance_id", previousInstanceId), ("count", count?.ToString()),
        ("count_sequence", sequence?.ToString()));

    public async Task<JObject> SendAsync(TestHttpRequest request)
    {
        var response = await BalancerApi.OnRequest(request);
        Assert.Equal(HttpStatusCode.OK, response.status);
        return JObject.Parse(Encoding.UTF8.GetString(response.data));
    }

    public Task<JObject> JoinAsync() => SendAsync(RoomRequest("/join"));

    public async Task<JObject?> ListedRoomAsync()
    {
        var result = await SendAsync(new TestHttpRequest("/list"));
        return result["results"]!.Children<JObject>().SingleOrDefault(room => (string?)room["name"] == Name);
    }

    public async Task WaitForCountAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            if ((int?)(await ListedRoomAsync())?["connectedPlayers"] == count)
                return;
            await Task.Delay(10);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal(count, (int?)(await ListedRoomAsync())?["connectedPlayers"]);
    }

    public Room CurrentRoom()
    {
        Assert.True(Lobby.TryGetRoom(Name, out var room));
        return Assert.IsType<Room>(room);
    }

    public PlayerInfo Authenticate(string secret, bool success = true, int backend = 1, bool nat = false,
        bool webRtcP2P = false, bool awaitingWebRtcP2P = false)
    {
        var player = new PlayerInfo(HTTPRestAPI.CreateCallbacks(backend).ReserveConnId(true), true);
        _players.Add(player);
        var data = Encoding.UTF8.GetBytes(new JObject
        {
            ["roomName"] = Name,
            ["clientSecret"] = secret,
            ["nat"] = nat,
            ["webRtcP2P"] = webRtcP2P
        }.ToString());
        Transport.OnServerReceivedData(player, data);
        var expected = awaitingWebRtcP2P ? SERVER_PACKET_TYPE.SERVER_WEBRTC_P2P :
            success ? SERVER_PACKET_TYPE.SERVER_AUTHENTICATED : SERVER_PACKET_TYPE.SERVER_AUTHENTICATION_FAILED;
        var recorder = Assert.IsType<RecordingUdpServer>(HTTPRestAPI.GetUdpServerForConnection(player.connId));
        Assert.Contains(recorder.Packets, packet => packet.Connection == player.connId && packet.Data[0] == (byte)expected);
        return player;
    }

    public static T ReadState<T>(Type type, string field) =>
        (T)type.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    public static T ReadRoomState<T>(Room room, string field)
    {
        lock (ReadState<object>(typeof(Lobby), "_roomLock"))
            return (T)typeof(Room).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(room)!;
    }

    public bool HasRelayRoom()
    {
        lock (ReadState<object>(typeof(Lobby), "_roomLock"))
            return ReadState<Dictionary<string, Room>>(typeof(Lobby), "_room").ContainsKey(Name);
    }

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for room lifecycle operation");
    }

    public static int Cleanup(Type type, DateTime now, TimeSpan timeout) =>
        (int)type.GetMethod("CleanupEmptyRooms", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [now, timeout])!;

    public async ValueTask DisposeAsync()
    {
        try
        {
            BeforeRegistration = null;
            BeforeUnregistration = null;
            FailFirstRegistration = false;
            FailFirstUnregistration = false;
            foreach (var player in _players)
                Transport.OnClientLeft(player);
            lock (ReadState<object>(typeof(Lobby), "_roomLock"))
            {
                if (ReadState<Dictionary<string, Room>>(typeof(Lobby), "_room").TryGetValue(Name, out var room))
                    Lobby.RemoveRoom(room.roomId);
            }
            // Wait for relay unregister and its local completion before changing
            // process-wide configuration or closing the balancer listener.
            await WaitUntilAsync(() => !HasRelayRoom());
            var servers = await SendAsync(new TestHttpRequest("/servers"));
            var currentServer = servers["servers"]!.Children<JObject>()
                .SingleOrDefault(server => (string?)server["apiEndpoint"] == Endpoint);
            await BalancerApi.OnRequest(ServerRequest("/unregisterServer", (string?)currentServer?["instanceId"]));
        }
        finally
        {
            HTTPRestAPI.webServer?.Dispose();
            HTTPRestAPI.webServer = _originalWebServer;
            HTTPRestAPI.udpServerV1 = _originalUdpServer;
            _listener.Close();
            foreach (var (name, value) in _originalEnvironment)
                Environment.SetEnvironmentVariable(name, value);
            await _listenTask;
            await Task.WhenAll(_requests);
        }
    }

    internal sealed class RecordingUdpServer : IUdpServer
    {
        public ConcurrentQueue<(int Connection, byte[] Data, byte Method)> Packets { get; } = new();
        public ConcurrentQueue<int> Kicked { get; } = new();
        public Action<int, byte[]>? BeforeSend { get; set; }
        public void SendOne(int connId, ReadOnlySpan<byte> data, byte deliveryMethod)
        {
            var packet = data.ToArray();
            BeforeSend?.Invoke(connId, packet);
            Packets.Enqueue((connId, packet, deliveryMethod));
        }
        public void KickClient(int connId) => Kicked.Enqueue(connId);
    }
}

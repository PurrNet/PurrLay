using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PurrLay.Tests;

// The test apphost doubles as a controllable child process for supervisor tests.
internal static class GatewayProbe
{
    public static async Task<int> Main()
    {
        var directory = Environment.GetEnvironmentVariable("PURR_GATEWAY_TEST_DIRECTORY");
        if (directory == null) return 0;
        try { return await RunAsync(directory); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Gateway probe failed: {exception.Message}");
            return 1;
        }
    }

    static async Task<int> RunAsync(string directory)
    {
        var pid = Environment.ProcessId;
        await File.AppendAllTextAsync(Path.Combine(directory, "starts"), $"{pid},{DateTime.UtcNow.Ticks}\n");
        if (File.Exists(Path.Combine(directory, "fail-start"))) return 23;

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback,
            int.Parse(Environment.GetEnvironmentVariable("WEBRTC_UDP_PORT")!)));
        using var http = new HttpListener();
        http.Prefixes.Add($"http://{Environment.GetEnvironmentVariable("WEBRTC_HTTP_ADDRESS")}/");
        http.Start();

        using var bridge = new TcpClient();
        await bridge.ConnectAsync(IPEndPoint.Parse(Environment.GetEnvironmentVariable("PURR_WEBRTC_BRIDGE_ADDRESS")!));
        var token = Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("PURR_WEBRTC_BRIDGE_TOKEN")!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = bridge.GetStream();
        await WriteFrameAsync(stream, 0, 0, token);
        var hello = await WebRtcServer.ReadFrameAsync(stream, timeout.Token);
        if (hello.Type != 0 || hello.Method != 0 || hello.Payload.Length != 0) return 24;
        await WriteFrameAsync(stream, 1, 2, Encoding.UTF8.GetBytes("{\"pipe\":true}"));
        var authenticated = await WebRtcServer.ReadFrameAsync(stream, timeout.Token);
        if (authenticated.Type != 1 || authenticated.Payload.Length != 5 || authenticated.Payload[0] != 5) return 25;
        var ready = new ProbeReady(BinaryPrimitives.ReadInt32LittleEndian(authenticated.Payload.AsSpan(1)),
            Convert.ToHexString(SHA256.HashData(token)));
        await File.WriteAllTextAsync(Path.Combine(directory, $"ready-{pid}"), JsonSerializer.Serialize(ready));

        while (true)
        {
            var context = await http.GetContextAsync();
            _ = HandleAsync(context, directory, pid);
        }
    }

    static async Task HandleAsync(HttpListenerContext context, string directory, int pid)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            if (path == "/health")
            {
                await File.WriteAllTextAsync(Path.Combine(directory, $"health-{pid}"), "requested");
                if (File.Exists(Path.Combine(directory, "hang-health")) ||
                    File.Exists(Path.Combine(directory, $"hang-health-{pid}")))
                    await Task.Delay(Timeout.Infinite);
                context.Response.StatusCode = File.Exists(Path.Combine(directory, $"unhealthy-{pid}")) ? 503 : 200;
            }
            else if (path == "/offer")
            {
                if (File.Exists(Path.Combine(directory, "hold-offer")))
                {
                    await File.WriteAllTextAsync(Path.Combine(directory, $"offer-{pid}"), "pending");
                    await Task.Delay(Timeout.Infinite);
                }
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"type\":\"answer\",\"sdp\":\"v=0\"}"));
            }
            else context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception exception) when (exception is IOException or HttpListenerException or ObjectDisposedException) { }
    }

    static async Task WriteFrameAsync(Stream stream, byte type, byte method, byte[] payload)
    {
        var frame = new byte[6 + payload.Length];
        frame[0] = type;
        frame[1] = method;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(frame, 6);
        await stream.WriteAsync(frame);
    }

    internal sealed record ProbeReady(int ConnectionId, string TokenHash);
}

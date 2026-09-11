using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using WatsonWebserver;
using WatsonWebserver.Core;

namespace PurrBalancer;

internal static class Program
{
    public static string SECRET_INTERNAL { get; private set; } = "PURRNET";

    private static async Task HandleRouting(HttpContextBase context)
    {
        await Console.Out.WriteLineAsync($"Received request: {context.Request.Method} {context.Request.Url.Full}");

        var req = context.Request;
        var resp = context.Response;

        try
        {
            var response = await HTTPRestAPI.OnRequest(req);
            context.Response.ContentLength = response.data.Length;
            context.Response.ContentType = response.contentType;
            context.Response.StatusCode = (int)response.status;
            await resp.Send(response.data);
        }
        catch (Exception e)
        {
            await HandleError(context, e);
        }
    }

    private static async Task HandleError(HttpContextBase context, Exception ex)
    {
        await Console.Error.WriteLineAsync($"Error handling request: {ex.Message}\n{ex.StackTrace}");
        string message = $"{ex.Message}\n{ex.StackTrace}";
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength = message.Length;
        await context.Response.Send(message);
    }

    static void Main(string[] args)
    {
        string? certPath = null;
        string? keyPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cert" when i + 1 < args.Length:
                    certPath = args[++i];
                    break;
                case "--key" when i + 1 < args.Length:
                    keyPath = args[++i];
                    break;
            }
        }

        try
        {
            var host = Env.TryGetValueOrDefault("HOST", "localhost");
            var port = Env.TryGetIntOrDefault("PORT", 8080);

            if (Env.TryGetValue("SECRET", out var secret) && secret != null)
                SECRET_INTERNAL = secret;
            HTTPRestAPI.ConfigureDeployment();

            var machine = Env.TryGetValueOrDefault("FLY_MACHINE_ID", "");
            var app = Env.TryGetValueOrDefault("FLY_APP_NAME", "");
            var privateHost = string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(app)
                ? null : $"{machine}.vm.{app}.internal";
            if (privateHost != null)
                WaitForPrivateAddress(privateHost);

            using var certificate = certPath != null && keyPath != null
                ? X509Certificate2.CreateFromPemFile(certPath, keyPath) : null;
            using var publicServer = CreateServer(host, port, certificate);
            publicServer.Start();
            Console.WriteLine($"Listening on {publicServer.Settings.Prefix}");

            // HttpListener's '+' prefix binds only IPv4 on Linux. The named
            // prefix resolves to this Machine's private IPv6 address.
            using var privateServer = privateHost != null && privateHost != host
                ? CreateServer(privateHost, port) : null;
            privateServer?.Start();
            if (privateServer != null)
                Console.WriteLine($"Listening on {privateServer.Settings.Prefix}");

            _ = HTTPRestAPI.InitializeDeploymentAsync();
            HTTPRestAPI.StartHealthCheckService();
            HTTPRestAPI.StartEmptyRoomCleanupService();
            Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error during startup: {e.Message}\n{e.StackTrace}");
            Environment.Exit(1);
        }
    }

    private static void WaitForPrivateAddress(string host)
    {
        var deadline = Environment.TickCount64 + 30000;
        Exception? lastError = null;
        do
        {
            try
            {
                var remaining = TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64));
                var addresses = Dns.GetHostAddressesAsync(host).WaitAsync(remaining).GetAwaiter().GetResult();
                if (addresses.Any(address => address.AddressFamily != AddressFamily.InterNetworkV6))
                    throw new InvalidOperationException($"Private listener hostname must resolve only to IPv6: {host}");
                if (addresses.Length > 0)
                    return;
            }
            catch (Exception error) when (error is SocketException or TimeoutException) { lastError = error; }
            Thread.Sleep(250);
        } while (Environment.TickCount64 < deadline);
        throw new InvalidOperationException($"Private listener hostname did not resolve: {host}", lastError);
    }

    private static Webserver CreateServer(string host, int port, X509Certificate2? certificate = null)
    {
        var settings = new WebserverSettings(host, port);
        settings.Headers.DefaultHeaders["Access-Control-Allow-Origin"] = "*";
        settings.Headers.DefaultHeaders["Access-Control-Allow-Methods"] = "*";
        settings.Headers.DefaultHeaders["Access-Control-Allow-Headers"] = "*";
        settings.Headers.DefaultHeaders["Access-Control-Allow-Credentials"] = "true";
        if (certificate != null)
            settings.Ssl = new WebserverSettings.SslSettings { Enable = true, SslCertificate = certificate };
        return new Webserver(settings, HandleRouting);
    }
}

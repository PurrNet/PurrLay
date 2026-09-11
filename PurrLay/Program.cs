using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PurrBalancer;
using WatsonWebserver;
using WatsonWebserver.Core;

namespace PurrLay;

internal static class Program
{
    public static string SECRET_INTERNAL { get; private set; } = "PURRNET";
    internal static readonly string ProcessInstanceId = Guid.NewGuid().ToString("N");

    private static async Task HandleRouting(HttpContextBase context)
    {
        await Console.Out.WriteLineAsync($"Received request: {context.Request.Method} {context.Request.Url.Full}");

        // Peel out the re quests and response objects
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

    [UsedImplicitly]
    struct RelayServer
    {
        [UsedImplicitly] public string instanceId;
        [UsedImplicitly] public string? deploymentId;
        [UsedImplicitly] public bool draining;
        [UsedImplicitly] public string apiEndpoint;
        [UsedImplicitly] public string host;
        [UsedImplicitly] public int udpPort;
        [UsedImplicitly] public int udpPortV2;
        [UsedImplicitly] public int webSocketsPort;
        [UsedImplicitly] public string region;
    }

    public static int UDP_PORT => ConfiguredPort("UDP_PORT", 7777);
    public static int UDP_PORT_V2 => ConfiguredPort("UDP_PORT_V2", 7778);
    public static int WEBSOCKETS_PORT => ConfiguredPort("WEBSOCKETS_PORT", 6942);

    static int ConfiguredPort(string name, int fallback)
    {
        var port = Env.TryGetIntOrDefault(name, fallback);
        return port is > 0 and <= ushort.MaxValue ? port : throw new ArgumentOutOfRangeException(name);
    }

    internal static string GetRelayEndpoint()
    {
        var ssl = Env.TryGetValueOrDefault("HOST_SSL", "false");
        var domain = Env.TryGetValueOrDefault("HOST_DOMAIN", Env.TryGetValueOrDefault("HOST", "localhost"));
        var hostPort = Env.TryGetIntOrDefault("HOST_PORT", -1);
        var urlPort = hostPort == -1 ? "" : $":{hostPort}";

        return Env.TryGetValueOrDefault("HOST_ENDPOINT",
            (ssl == "true" ? "https://" : "http://") + domain + urlPort);
    }

    static async void RegisterRelayToBalancer()
    {
        try
        {
            const int SECONDS_BETWEEN_REGISTRATION_ATTEMPTS = 30;

            if (!Env.TryGetValue("BALANCER_URL", out var balancer) || balancer == null)
            {
                await Console.Error.WriteLineAsync("Missing `BALANCER_URL` env variable");
                return;
            }

            if (!Env.TryGetValue("HOST_SSL", out var ssl) || ssl == null)
            {
                await Console.Error.WriteLineAsync("Missing `HOST_SSL` env variable");
                return;
            }

            if (!Env.TryGetValue("HOST_REGION", out var region)  || region == null)
            {
                await Console.Error.WriteLineAsync("Missing `HOST_REGION` env variable");
                return;
            }

            if (!Env.TryGetValue("HOST_DOMAIN", out var domain)  || domain == null)
            {
                await Console.Error.WriteLineAsync("Missing `HOST_DOMAIN` env variable");
                return;
            }

            var server = new RelayServer
            {
                instanceId = ProcessInstanceId,
                apiEndpoint = GetRelayEndpoint(),
                host = domain,
                udpPort = UDP_PORT,
                udpPortV2 = UDP_PORT_V2,
                webSocketsPort = WEBSOCKETS_PORT,
                region = region
            };

            while (true)
            {
                try
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Add("internal_key_secret", SECRET_INTERNAL);

                    var administrationRevision = RelayDeployment.AdministrationRevision;
                    server.deploymentId = RelayDeployment.DeploymentId;
                    server.draining = RelayDeployment.Draining;
                    var serverJson = JsonConvert.SerializeObject(server);
                    using var content = new StringContent(serverJson, Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync($"{balancer}/registerServer", content);

                    if (!response.IsSuccessStatusCode)
                        await Console.Error.WriteLineAsync(
                            $"Failed to register server: [{response.StatusCode}] {await response.Content.ReadAsStringAsync()}");
                    else
                        RelayDeployment.ApplyRegistrationResponse(JObject.Parse(await response.Content.ReadAsStringAsync()), administrationRevision);
                }
                catch (Exception e)
                {
                    await Console.Error.WriteLineAsync($"Error registering server: {e.Message}\n{e.StackTrace}");
                }

                await Task.Delay(SECONDS_BETWEEN_REGISTRATION_ATTEMPTS * 1000);
            }
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"Error registering server: {e.Message}\n{e.StackTrace}");
        }
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
            if (Env.TryGetValue("SECRET", out var secret) && secret != null)
                SECRET_INTERNAL = secret;

            RelayDeployment.Initialize(Env.TryGetValueOrDefault("RELAY_DEPLOYMENT_ID", ""),
                bool.TryParse(Env.TryGetValueOrDefault("RELAY_START_STANDBY", "false"), out var standby) && standby);
            HTTPRestAPI.webServer = new WebSockets(WEBSOCKETS_PORT);
            HTTPRestAPI.udpServerV1 = UdpServerFactory.CreateV1(UDP_PORT, HTTPRestAPI.CreateCallbacks(1));
            HTTPRestAPI.udpServerV2 = UdpServerFactory.CreateV2(UDP_PORT_V2, HTTPRestAPI.CreateCallbacks(2));
            HTTPRestAPI.webRtcRuntime = WebRtcGatewayRuntime.StartAsync().GetAwaiter().GetResult();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => HTTPRestAPI.webRtcRuntime?.Dispose();

            var host = Env.TryGetValueOrDefault("HOST", "localhost");
            var port = Env.TryGetIntOrDefault("PORT", 8081);

            Console.WriteLine($"Listening on http://{host}:{port}/");

            var settings = new WebserverSettings(host, port);
            settings.Headers.DefaultHeaders["Access-Control-Allow-Origin"] = "*";
            settings.Headers.DefaultHeaders["Access-Control-Allow-Methods"] = "*";
            settings.Headers.DefaultHeaders["Access-Control-Allow-Headers"] = "*";
            settings.Headers.DefaultHeaders["Access-Control-Allow-Credentials"] = "true";

            if (certPath != null && keyPath != null)
            {
                settings.Ssl = new WebserverSettings.SslSettings
                {
                    Enable = true,
                    SslCertificate = X509Certificate2.CreateFromPemFile(certPath, keyPath)
                };
            }

            new Webserver(settings, HandleRouting).Start();
            RegisterRelayToBalancer();
            
            // Start empty room cleanup task
            _ = Lobby.StartEmptyRoomCleanupTask();
            
            Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error during startup: {e.Message}\n{e.StackTrace}");
            Environment.Exit(1);
        }
    }
}

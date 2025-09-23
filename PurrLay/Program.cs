using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using JetBrains.Annotations;
using Newtonsoft.Json;
using PurrCommon;
using WatsonWebserver;
using WatsonWebserver.Core;

namespace PurrLay;

internal static class Program
{
    public static string SECRET_INTERNAL { get; private set; } = "PURRNET";

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
        [UsedImplicitly] public string apiEndpoint;
        [UsedImplicitly] public string host;
        [UsedImplicitly] public int udpPort;
        [UsedImplicitly] public int webSocketsPort;
        [UsedImplicitly] public string region;
    }

    public const int UDP_PORT = 7777;

    static async void RegisterRelayToBalancer()
    {
        try
        {
            const int SECONDS_BETWEEN_REGISTRATION_ATTEMPTS = 30;

            if (!Env.TryGetValue("BALANCER_URL", out var balancer) || string.IsNullOrEmpty(balancer))
            {
                await Console.Error.WriteLineAsync("Missing `BALANCER_URL` env variable");
                return;
            }

            if (!Env.TryGetValue("HOST_SSL", out var ssl) || string.IsNullOrEmpty(ssl))
            {
                await Console.Error.WriteLineAsync("Missing `HOST_SSL` env variable");
                return;
            }

            if (!Env.TryGetValue("HOST_REGION", out var region)  || string.IsNullOrEmpty(region))
            {
                await Console.Error.WriteLineAsync("Missing `HOST_REGION` env variable");
                return;
            }

            if (!Env.TryGetValue("HOST_DOMAIN", out var domain)  || string.IsNullOrEmpty(domain))
            {
                await Console.Error.WriteLineAsync("Missing `HOST_DOMAIN` env variable");
                return;
            }

            var hostPort = Env.TryGetIntOrDefault("HOST_PORT", -1);
            string urlPort = hostPort == -1 ? "" : $":{hostPort}";

            string endpoint = Env.TryGetValueOrDefault("HOST_ENDPOINT", (ssl == "true" ? "https://" : "http://") + domain + urlPort);

            var server = new RelayServer
            {
                apiEndpoint = endpoint,
                host = domain,
                udpPort = UDP_PORT,
                webSocketsPort = 6942,
                region = region
            };

            var serverJson = JsonConvert.SerializeObject(server);

            while (true)
            {
                try
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Add("internal_key_secret", SECRET_INTERNAL);

                    using var content = new StringContent(serverJson, Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync($"{balancer}/registerServer", content);

                    if (!response.IsSuccessStatusCode)
                        await Console.Error.WriteLineAsync(
                            $"Failed to register server: [{response.StatusCode}] {await response.Content.ReadAsStringAsync()}");
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
        try
        {
            if (Env.TryGetValue("SECRET", out var secret) && secret != null)
                SECRET_INTERNAL = secret;

            RegisterRelayToBalancer();

            var host = Env.TryGetValueOrDefault("HOST", "localhost");
            var port = Env.TryGetIntOrDefault("PORT", 8081);

            Console.WriteLine($"Listening on http://{host}:{port}/");

            var settings = new WebserverSettings(host, port);
            settings.Headers.DefaultHeaders["Access-Control-Allow-Origin"] = "*";
            settings.Headers.DefaultHeaders["Access-Control-Allow-Methods"] = "*";
            settings.Headers.DefaultHeaders["Access-Control-Allow-Headers"] = "*";
            settings.Headers.DefaultHeaders["Access-Control-Allow-Credentials"] = "true";

            // SSL configuration via .env only
            if (Env.TryGetValue("SSL_CERT_PATH", out var certPath) &&
                Env.TryGetValue("SSL_KEY_PATH", out var keyPath) &&
                !string.IsNullOrWhiteSpace(certPath) &&
                !string.IsNullOrWhiteSpace(keyPath))
            {
                try
                {
                    settings.Ssl = new WebserverSettings.SslSettings
                    {
                        Enable = true,
                        SslCertificate = X509Certificate2.CreateFromPemFile(certPath!, keyPath!)
                    };
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Failed to load SSL certificate from .env paths: {e.Message}");
                }
            }

            new Webserver(settings, HandleRouting).Start();
            Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error during startup: {e.Message}\n{e.StackTrace}");
            Environment.Exit(1);
        }
    }
}

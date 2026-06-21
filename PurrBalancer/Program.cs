using System.Net;
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
            HTTPRestAPI.StartHealthCheckService();
            HTTPRestAPI.StartEmptyRoomCleanupService();

            var host = Env.TryGetValueOrDefault("HOST", "localhost");
            var port = Env.TryGetIntOrDefault("PORT", 8080);

            if (Env.TryGetValue("SECRET", out var secret) && secret != null)
                SECRET_INTERNAL = secret;

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
            Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error during startup: {e.Message}\n{e.StackTrace}");
            Environment.Exit(1);
        }
    }
}

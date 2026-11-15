using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace PurrLay;

/// <summary>
/// Relay numbers for monitoring: open rooms, players in rooms (hosts included), open connections, room joins, and the
/// bytes received from players and handed on to other players and hosts. Sent over OTLP only when
/// OTEL_EXPORTER_OTLP_ENDPOINT is set; the other standard OTEL_* environment variables apply too, e.g.
/// OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf for Prometheus, OTEL_SERVICE_NAME (purrlay when unset) and
/// OTEL_RESOURCE_ATTRIBUTES=service.instance.id=... for a name that stays the same across restarts.
/// </summary>
internal static class RelayMetrics
{
    internal const string METER = "PurrLay";

    static readonly Meter _meter = new(METER);
    static readonly Counter<long> _received = _meter.CreateCounter<long>("purrlay.received", "By", "Bytes received from players");
    static readonly Counter<long> _relayed = _meter.CreateCounter<long>("purrlay.relayed", "By", "Bytes handed on to other players and hosts");
    static readonly Counter<long> _joins = _meter.CreateCounter<long>("purrlay.player.joins", "{join}", "Room joins, hosts included");

    static MeterProvider? _provider;

    static RelayMetrics()
    {
        _meter.CreateObservableUpDownCounter("purrlay.room.count", () => Lobby.GetDeploymentCounts().Rooms, "{room}", "Open rooms");
        _meter.CreateObservableUpDownCounter("purrlay.player.count", Transport.GetTotalConnectionCount, "{player}", "Players in rooms, hosts included");
        _meter.CreateObservableUpDownCounter("purrlay.connection.count", Transport.GetTransportConnectionCount, "{connection}", "Open connections");
    }

    public static void Start()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
            return;

        _provider = Sdk.CreateMeterProviderBuilder()
            // The OTEL_* variables come last, so they can rename the service or give it a stable instance id.
            .SetResourceBuilder(ResourceBuilder.CreateEmpty()
                .AddService("purrlay", serviceInstanceId: Program.ProcessInstanceId)
                .AddTelemetrySdk()
                .AddEnvironmentVariableDetector())
            .AddMeter(METER)
            .AddOtlpExporter()
            .Build();
        // Sends what was counted since the last export before the process ends.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _provider?.Dispose();
        Console.WriteLine("Sending metrics over OTLP.");
    }

    public static void Received(int bytes) => _received.Add(bytes);
    public static void Relayed(int bytes) => _relayed.Add(bytes);
    public static void Joined() => _joins.Add(1);
}

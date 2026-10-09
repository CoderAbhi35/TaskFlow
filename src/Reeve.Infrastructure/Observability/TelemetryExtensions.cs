using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Reeve.Application.Telemetry;

namespace Reeve.Infrastructure.Observability;

/// <summary>Configuration section <c>Telemetry</c>.</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public bool Enabled { get; set; } = true;

    /// <summary>OTLP (gRPC) endpoint of the OpenTelemetry Collector.</summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>Fraction of new traces to keep (0-1). Continued traces follow their parent's decision.</summary>
    public double SamplingRatio { get; set; } = 1.0;

    public int MetricExportIntervalMs { get; set; } = 10_000;

    /// <summary>How long one export (and the final flush at shutdown) may wait for the collector.</summary>
    public int ExportTimeoutMs { get; set; } = 10_000;
}

public static class TelemetryExtensions
{
    /// <summary>
    /// Traces and metrics for one Reeve process, pushed over OTLP to the collector. Export happens
    /// in the background: if the collector is down, telemetry is dropped and the service carries on.
    /// Each host adds its own instrumentation (e.g. ASP.NET Core) through the callbacks.
    /// </summary>
    public static IHostApplicationBuilder AddReeveTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? tracing = null,
        Action<MeterProviderBuilder>? metrics = null)
    {
        var options = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new();

        // Every log line written inside a span carries its trace and span IDs, so logs and traces join up.
        builder.Logging.Configure(o =>
            o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

        if (!options.Enabled)
            return builder;

        var otlp = new Uri(options.OtlpEndpoint);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: typeof(TelemetryExtensions).Assembly.GetName().Version?.ToString(),
                    serviceInstanceId: $"{Environment.MachineName}-{Environment.ProcessId}".ToLowerInvariant())
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(t =>
            {
                t.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.SamplingRatio)))
                    .AddSource(ReeveTelemetry.Name)
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddOtlpExporter(o => { o.Endpoint = otlp; o.Protocol = OtlpExportProtocol.Grpc; o.TimeoutMilliseconds = options.ExportTimeoutMs; });
                tracing?.Invoke(t);
            })
            .WithMetrics(m =>
            {
                m.AddMeter(ReeveTelemetry.Name)
                    .AddRuntimeInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter((o, reader) =>
                    {
                        o.Endpoint = otlp;
                        o.Protocol = OtlpExportProtocol.Grpc;
                        o.TimeoutMilliseconds = options.ExportTimeoutMs;
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = options.MetricExportIntervalMs;
                    });
                metrics?.Invoke(m);
            });

        return builder;
    }
}

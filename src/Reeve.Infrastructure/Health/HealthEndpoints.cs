using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Reeve.Infrastructure.Health;

/// <summary>
/// Liveness and readiness for every Reeve process, with the meaning Kubernetes gives them:
/// <list type="bullet">
/// <item><b>live</b>: the process isn't stuck. Failing it gets the container restarted, so it never
/// depends on PostgreSQL, Kafka or Redis; an outage would otherwise restart every pod at once.</item>
/// <item><b>ready</b>: the process can do its job now (reach the database, heartbeat, dispatch).
/// Failing it only takes the pod out of service and holds up a rolling update.</item>
/// </list>
/// </summary>
public static class HealthEndpoints
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    /// <summary>Liveness: every loop registered with <see cref="LoopMonitor"/> has completed a pass recently.</summary>
    public static IHealthChecksBuilder AddLoopsAlive(this IHealthChecksBuilder builder) =>
        builder.Add(new HealthCheckRegistration("loops",
            sp => new LoopsAliveHealthCheck(sp.GetRequiredService<LoopMonitor>()), HealthStatus.Unhealthy, [LiveTag]));

    /// <summary>Readiness: the named loop has completed a successful pass within <paramref name="maxAge"/>.</summary>
    public static IHealthChecksBuilder AddLoopSucceeded(this IHealthChecksBuilder builder, string loop, TimeSpan maxAge) =>
        builder.Add(new HealthCheckRegistration($"{loop} succeeded",
            sp => new LoopSucceededHealthCheck(sp.GetRequiredService<LoopMonitor>(), loop, maxAge), HealthStatus.Unhealthy, [ReadyTag]));

    /// <summary>Maps <c>{prefix}/live</c> and <c>{prefix}/ready</c>, open to anonymous probes.</summary>
    public static void MapReeveHealth(this IEndpointRouteBuilder endpoints, string prefix)
    {
        endpoints.MapHealthChecks($"{prefix}/live", Options(LiveTag)).AllowAnonymous();
        endpoints.MapHealthChecks($"{prefix}/ready", Options(ReadyTag)).AllowAnonymous();
    }

    private static HealthCheckOptions Options(string tag) => new()
    {
        Predicate = check => check.Tags.Contains(tag),
        ResponseWriter = WriteJsonAsync,
    };

    /// <summary>Status plus each check's result, so a failing probe says which loop or dependency failed.</summary>
    private static Task WriteJsonAsync(HttpContext http, HealthReport report)
    {
        http.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new
            {
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data.Count == 0 ? null : e.Value.Data,
            }),
        };
        return http.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string Age(TimeSpan age) => $"{age.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s ago";
}

internal sealed class LoopsAliveHealthCheck(LoopMonitor monitor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = monitor.Now;
        var loops = monitor.Snapshot();
        var data = loops.ToDictionary(l => l.Name, l => (object)$"last pass {HealthEndpoints.Age(now - l.LastBeat)}");
        var stuck = loops.Where(l => now - l.LastBeat > l.StuckAfter).Select(l => l.Name).ToList();

        return Task.FromResult(stuck.Count == 0
            ? HealthCheckResult.Healthy($"{loops.Count} loop(s) running", data)
            : HealthCheckResult.Unhealthy($"Stuck: {string.Join(", ", stuck)}", data: data));
    }
}

internal sealed class LoopSucceededHealthCheck(LoopMonitor monitor, string loop, TimeSpan maxAge) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = monitor.Snapshot().FirstOrDefault(l => l.Name == loop);
        var now = monitor.Now;

        return Task.FromResult(status?.LastSuccess switch
        {
            null => HealthCheckResult.Unhealthy($"{loop} has not succeeded yet"),
            { } at when now - at > maxAge => HealthCheckResult.Unhealthy($"{loop} last succeeded {HealthEndpoints.Age(now - at)}"),
            { } at => HealthCheckResult.Healthy($"{loop} succeeded {HealthEndpoints.Age(now - at)}"),
        });
    }
}

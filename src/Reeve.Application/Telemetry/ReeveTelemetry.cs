using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Reeve.Application.Telemetry;

/// <summary>
/// Reeve's own traces and metrics, defined with the BCL types (System.Diagnostics) so the
/// application layer does not depend on OpenTelemetry; the hosts subscribe to <see cref="Name"/>.
/// </summary>
/// <remarks>
/// Metric names follow OpenTelemetry conventions; Prometheus sees them as, e.g.,
/// <c>reeve_jobs_submitted_total</c> and <c>reeve_job_execution_duration_seconds</c>.
/// </remarks>
public static class ReeveTelemetry
{
    public const string Name = "Reeve";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    // Tag keys, kept short because they become Prometheus labels.
    public const string JobTypeTag = "job_type";
    public const string OutcomeTag = "outcome";
    public const string SourceTag = "source";
    public const string StateTag = "state";
    public const string WorkerTag = "worker_id";
    public const string PolicyTag = "policy";

    public static readonly Counter<long> JobsSubmitted =
        Meter.CreateCounter<long>("reeve.jobs.submitted", "{job}", "Jobs created (by the API or a schedule).");

    public static readonly Counter<long> JobsSucceeded =
        Meter.CreateCounter<long>("reeve.jobs.succeeded", "{job}", "Jobs that completed successfully.");

    /// <summary>Tagged <c>outcome=failed</c> (permanent error) or <c>outcome=dead_lettered</c> (retries exhausted).</summary>
    public static readonly Counter<long> JobsFailed =
        Meter.CreateCounter<long>("reeve.jobs.failed", "{job}", "Jobs that ended without succeeding.");

    public static readonly Counter<long> JobsRetried =
        Meter.CreateCounter<long>("reeve.jobs.retried", "{job}", "Failed attempts that scheduled an automatic retry.");

    public static readonly Counter<long> JobsRecovered =
        Meter.CreateCounter<long>("reeve.jobs.recovered", "{job}", "Running jobs taken back from workers that stopped heartbeating.");

    public static readonly Counter<long> JobsDispatched =
        Meter.CreateCounter<long>("reeve.jobs.dispatched", "{job}", "Jobs published to the transport.");

    public static readonly Histogram<double> JobExecutionDuration = Meter.CreateHistogram<double>(
        "reeve.job.execution.duration", "s", "Time spent executing one attempt of a job.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300] });

    public static readonly Counter<long> RateLimited =
        Meter.CreateCounter<long>("reeve.api.rate_limited", "{request}", "Requests rejected by a rate limit policy.");

    // ---- trace context carried across processes ---------------------------------------------

    /// <summary>The W3C traceparent of the current span, to be stored with work that continues later elsewhere.</summary>
    public static string? CurrentTraceParent() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current.Id : null;

    /// <summary>Continues a stored or propagated trace; a missing or malformed value starts a new one.</summary>
    public static Activity? StartContinuing(string name, ActivityKind kind, string? traceParent)
    {
        var parent = traceParent is not null && ActivityContext.TryParse(traceParent, null, out var context)
            ? context
            : default;
        return Source.StartActivity(name, kind, parent);
    }
}

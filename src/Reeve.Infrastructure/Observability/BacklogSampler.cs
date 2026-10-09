using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reeve.Application.Abstractions;
using Reeve.Application.Telemetry;
using Reeve.Contracts.Queues;
using Reeve.Contracts.Workers;
using Reeve.Infrastructure.Health;

namespace Reeve.Infrastructure.Observability;

/// <summary>
/// Publishes system-wide state as gauges: queue depth per job type and state, and heartbeat age per
/// worker. It samples PostgreSQL on its own schedule and the gauges report the last sample, so a
/// metrics export never triggers a database query. Runs in the scheduler.
/// </summary>
public sealed partial class BacklogSampler : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private const string LoopName = "Backlog sampler";

    private readonly IServiceScopeFactory _scopes;
    private readonly LoopMonitor _loops;
    private readonly ILogger<BacklogSampler> _logger;
    private volatile Snapshot _latest = Snapshot.Empty;

    public BacklogSampler(IServiceScopeFactory scopes, LoopMonitor loops, ILogger<BacklogSampler> logger)
    {
        _scopes = scopes;
        _loops = loops;
        _logger = logger;

        ReeveTelemetry.Meter.CreateObservableGauge("reeve.queue.depth", ObserveQueues, "{job}",
            "Jobs per job type and state (ready, scheduled, queued, running, dead_lettered).");
        ReeveTelemetry.Meter.CreateObservableGauge("reeve.queue.oldest_waiting_age", ObserveOldestWaiting, "s",
            "How long the oldest job of each type that is due (ready or queued) has waited to be picked up.");
        ReeveTelemetry.Meter.CreateObservableGauge("reeve.worker.heartbeat.age", ObserveHeartbeats, "s",
            "Seconds since each non-offline worker last sent a heartbeat.");
        ReeveTelemetry.Meter.CreateObservableGauge("reeve.workers", ObserveWorkers, "{worker}",
            "Workers by status (active, draining, stale).");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        _loops.Register(LoopName, Interval);
        try
        {
            do
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var queries = scope.ServiceProvider.GetRequiredService<IJobQueries>();
                    _latest = new Snapshot(
                        await queries.GetQueueStatsAsync(stoppingToken),
                        await queries.GetWorkersAsync(stoppingToken));
                    _loops.Succeeded(LoopName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogSampleFailed(ex);
                    _loops.Beat(LoopName);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private IEnumerable<Measurement<long>> ObserveQueues()
    {
        foreach (var q in _latest.Queues)
        {
            var type = new KeyValuePair<string, object?>(ReeveTelemetry.JobTypeTag, q.JobType);
            yield return State(q.Ready, "ready");
            yield return State(q.Scheduled, "scheduled");
            yield return State(q.Queued, "queued");
            yield return State(q.Running, "running");
            yield return State(q.DeadLettered, "dead_lettered");

            Measurement<long> State(int value, string state) =>
                new(value, type, new(ReeveTelemetry.StateTag, state));
        }
    }

    /// <summary>Zero when nothing is waiting, so the series does not disappear when a queue drains.</summary>
    private IEnumerable<Measurement<double>> ObserveOldestWaiting() =>
        _latest.Queues.Select(q => new Measurement<double>(q.OldestWaitingAgeSeconds ?? 0,
            new KeyValuePair<string, object?>(ReeveTelemetry.JobTypeTag, q.JobType)));

    private IEnumerable<Measurement<double>> ObserveHeartbeats() =>
        _latest.Workers
            .Where(w => w.Status != WorkerStatus.Offline)
            .Select(w => new Measurement<double>(w.HeartbeatAgeSeconds, new KeyValuePair<string, object?>(ReeveTelemetry.WorkerTag, w.Id)));

    private IEnumerable<Measurement<long>> ObserveWorkers()
    {
        var workers = _latest.Workers.Where(w => w.Status != WorkerStatus.Offline).ToList();
        yield return Count(workers.Count(w => w.Status == WorkerStatus.Active && !w.IsStale), "active");
        yield return Count(workers.Count(w => w.Status == WorkerStatus.Draining && !w.IsStale), "draining");
        yield return Count(workers.Count(w => w.IsStale), "stale");

        static Measurement<long> Count(int value, string status) => new(value, new KeyValuePair<string, object?>("status", status));
    }

    private sealed record Snapshot(IReadOnlyList<QueueStatsResponse> Queues, IReadOnlyList<WorkerResponse> Workers)
    {
        public static readonly Snapshot Empty = new([], []);
    }

    [LoggerMessage(LogLevel.Warning, "Could not sample backlog and worker metrics")]
    private partial void LogSampleFailed(Exception ex);
}

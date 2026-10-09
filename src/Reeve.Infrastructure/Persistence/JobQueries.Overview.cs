using Microsoft.EntityFrameworkCore;
using Reeve.Contracts.Stats;
using DomainModel = Reeve.Domain.Jobs;
using DomainWorkers = Reeve.Domain.Workers;

namespace Reeve.Infrastructure.Persistence;

internal sealed partial class JobQueries
{
    public async Task<OverviewResponse> GetOverviewAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var cutoff = now - window;
        var windowMinutes = (int)window.TotalMinutes;
        // At most ~60 buckets whatever the window, so the chart stays readable.
        var bucketMinutes = Math.Max(1, (int)Math.Ceiling(windowMinutes / 60.0));
        var bucket = TimeSpan.FromMinutes(bucketMinutes);

        var submitted = await db.Jobs.CountAsync(j => j.CreatedAt >= cutoff, cancellationToken);

        var finished = await db.Jobs.AsNoTracking()
            .Where(j => j.CompletedAt >= cutoff)
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);
        int Finished(DomainModel.JobStatus status) => finished.GetValueOrDefault(status);

        var failedAttempts = await db.JobAttempts.CountAsync(
            a => a.Status == DomainModel.JobAttemptStatus.Failed && a.EndedAt >= cutoff, cancellationToken);

        var durations = await db.Database.SqlQuery<DurationRow>($"""
            SELECT
                percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM ended_at - started_at) * 1000) AS p50,
                percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM ended_at - started_at) * 1000) AS p95
            FROM job_attempts
            WHERE status = 'Succeeded' AND ended_at >= {cutoff}
            """).SingleAsync(cancellationToken);

        var rows = await db.Database.SqlQuery<BucketRow>($"""
            SELECT
                date_bin({bucket}, completed_at, TIMESTAMPTZ '2000-01-01 00:00:00+00') AS start,
                count(*) FILTER (WHERE status = 'Succeeded') AS succeeded,
                count(*) FILTER (WHERE status IN ('Failed', 'DeadLettered')) AS failed
            FROM jobs
            WHERE completed_at >= {cutoff}
            GROUP BY 1
            """).ToDictionaryAsync(r => r.Start, cancellationToken);

        var backlog = await db.Jobs.AsNoTracking()
            .Where(j => BacklogStatuses.Contains(j.Status))
            .GroupBy(_ => 1)
            .Select(g => new BacklogSummary(
                g.Count(j => j.Status == DomainModel.JobStatus.Pending && (j.ScheduledAt == null || j.ScheduledAt <= now)),
                g.Count(j => j.Status == DomainModel.JobStatus.Pending && j.ScheduledAt > now),
                g.Count(j => j.Status == DomainModel.JobStatus.Queued),
                g.Count(j => j.Status == DomainModel.JobStatus.Running),
                g.Count(j => j.Status == DomainModel.JobStatus.DeadLettered)))
            .SingleOrDefaultAsync(cancellationToken) ?? new BacklogSummary(0, 0, 0, 0, 0);

        var workers = await db.Workers.AsNoTracking().ToListAsync(cancellationToken);
        var timeout = workerHealth.Value.HeartbeatTimeout;
        var live = workers.Where(w => w.Status != DomainWorkers.WorkerStatus.Offline && !w.IsStale(now, timeout)).ToList();
        var workerSummary = new WorkerSummary(
            Active: live.Count(w => w.Status == DomainWorkers.WorkerStatus.Active),
            Draining: live.Count(w => w.Status == DomainWorkers.WorkerStatus.Draining),
            Stale: workers.Count(w => w.Status != DomainWorkers.WorkerStatus.Offline && w.IsStale(now, timeout)),
            Offline: workers.Count(w => w.Status == DomainWorkers.WorkerStatus.Offline),
            TotalConcurrency: live.Where(w => w.Status == DomainWorkers.WorkerStatus.Active).Sum(w => w.Concurrency));

        var succeeded = Finished(DomainModel.JobStatus.Succeeded);
        var failed = Finished(DomainModel.JobStatus.Failed);
        var deadLettered = Finished(DomainModel.JobStatus.DeadLettered);
        var outcomes = succeeded + failed + deadLettered;

        return new OverviewResponse(
            windowMinutes,
            now,
            submitted,
            succeeded,
            failed,
            deadLettered,
            Finished(DomainModel.JobStatus.Cancelled),
            failedAttempts,
            outcomes == 0 ? null : (double)succeeded / outcomes,
            outcomes / window.TotalMinutes,
            durations.P50,
            durations.P95,
            backlog,
            workerSummary,
            bucketMinutes,
            BuildSeries(rows, cutoff, now, bucket));
    }

    /// <summary>One entry per bucket across the whole window, including empty ones, so charts don't skip gaps.</summary>
    private static List<ThroughputBucket> BuildSeries(
        Dictionary<DateTimeOffset, BucketRow> rows, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket)
    {
        var origin = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = origin + TimeSpan.FromTicks((from - origin).Ticks / bucket.Ticks * bucket.Ticks);
        var series = new List<ThroughputBucket>();
        for (var start = first; start <= to; start += bucket)
        {
            rows.TryGetValue(start, out var row);
            series.Add(new ThroughputBucket(start, (int)(row?.Succeeded ?? 0), (int)(row?.Failed ?? 0)));
        }
        return series;
    }

    private sealed class DurationRow
    {
        public double? P50 { get; init; }
        public double? P95 { get; init; }
    }

    private sealed class BucketRow
    {
        public DateTimeOffset Start { get; init; }
        public long Succeeded { get; init; }
        public long Failed { get; init; }
    }
}

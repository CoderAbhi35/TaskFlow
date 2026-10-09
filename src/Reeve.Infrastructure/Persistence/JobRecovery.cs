using Microsoft.EntityFrameworkCore;
using Reeve.Application.Abstractions;
using Reeve.Application.Telemetry;
using Reeve.Domain.Workers;

namespace Reeve.Infrastructure.Persistence;

/// <summary>
/// Returns stuck work to the queue: jobs held by crashed workers, and attempts whose result was never
/// recorded. Every worker runs this; row locks with SKIP LOCKED mean two workers never recover the
/// same job, and the attempt-number check in the domain means a slow-but-alive worker's late result
/// cannot overwrite the retry.
/// </summary>
internal sealed class JobRecovery(ReeveDbContext db, TimeProvider time) : IJobRecovery
{
    private const int BatchSize = 100;

    public async Task<RecoveryResult> RecoverAsync(TimeSpan heartbeatTimeout, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var cutoff = now - heartbeatTimeout;

        var workersMarkedOffline = await MarkStaleWorkersOfflineAsync(cutoff, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The executor always ends an attempt by its timeout, so an attempt still open well past it lost
        // its result (recording failed on a live worker, or the handler never returned). Without this,
        // such a job would stay Running for as long as its worker keeps heartbeating.
        var overdueGrace = heartbeatTimeout * 2;

        // Running jobs whose open attempt belongs to a worker that is gone or silent, or is overdue.
        var candidates = await db.Database.SqlQuery<RecoveryCandidate>($"""
            SELECT j.id AS id,
                   (w.id IS NOT NULL AND w.last_heartbeat_at >= {cutoff}) AS worker_alive,
                   COALESCE(t.timeout_seconds, {(int)JobClaimer.DefaultTimeout.TotalSeconds}) AS timeout_seconds
            FROM jobs j
            JOIN job_attempts a ON a.job_id = j.id AND a.status = 'Running'
            LEFT JOIN workers w ON w.id = a.worker_id
            LEFT JOIN job_types t ON t.type = j.type
            WHERE j.status = 'Running'
              AND (w.id IS NULL
                   OR w.last_heartbeat_at < {cutoff}
                   OR a.started_at < {now} - make_interval(secs => COALESCE(t.timeout_seconds, {(int)JobClaimer.DefaultTimeout.TotalSeconds})) - {overdueGrace})
            ORDER BY j.id
            LIMIT {BatchSize}
            FOR UPDATE OF j SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return new RecoveryResult(workersMarkedOffline, 0);

        var byId = candidates.ToDictionary(c => c.Id);
        var jobs = await db.Jobs.Include(j => j.Attempts).Where(j => byId.Keys.Contains(j.Id)).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            var attempt = job.CurrentAttempt!;
            var candidate = byId[job.Id];
            var reason = candidate.WorkerAlive
                ? $"Attempt {attempt.AttemptNumber} ran past its {candidate.TimeoutSeconds} s timeout without recording a result."
                : $"Worker '{attempt.WorkerId}' stopped responding (no heartbeat for {heartbeatTimeout.TotalSeconds:0} s).";
            job.Fail(reason, isTransient: true, now, Random.Shared, attempt.AttemptNumber);
            ReeveTelemetry.JobsRecovered.Add(1, new KeyValuePair<string, object?>(ReeveTelemetry.JobTypeTag, job.Type));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RecoveryResult(workersMarkedOffline, jobs.Count);
    }

    private sealed record RecoveryCandidate(Guid Id, bool WorkerAlive, int TimeoutSeconds);

    private async Task<int> MarkStaleWorkersOfflineAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var stale = await db.Workers
            .Where(w => w.Status != WorkerStatus.Offline && w.LastHeartbeatAt < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var worker in stale)
            worker.MarkOffline();

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return stale.Count;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker's recovery pass got there first; nothing to do.
            db.ChangeTracker.Clear();
            return 0;
        }
    }
}

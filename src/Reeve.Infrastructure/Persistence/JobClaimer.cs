using Microsoft.EntityFrameworkCore;
using Reeve.Application.Abstractions;

namespace Reeve.Infrastructure.Persistence;

/// <summary>
/// Claims work under row locks held until the attempt is committed. Polling uses SKIP LOCKED so
/// workers never receive the same job and never block each other; claiming a specific job waits for
/// the lock instead, because the only other holder is the dispatcher that is publishing it right now.
/// </summary>
/// <remarks>
/// Both paths accept Pending and Queued jobs that are due. Queued jobs are normally claimed through
/// their transport message, but accepting them here too means polling and Kafka workers can coexist.
/// </remarks>
internal sealed class JobClaimer(ReeveDbContext db, TimeProvider time) : IJobClaimer
{
    /// <summary>Used only if a job type definition was removed after its jobs were created.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string workerId, IReadOnlyCollection<string> jobTypes, int maxJobs, CancellationToken cancellationToken = default)
    {
        if (maxJobs < 1 || jobTypes.Count == 0)
            return [];

        var now = time.GetUtcNow();
        var types = jobTypes.ToArray();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Served by ix_jobs_status_priority_created_at. Status values are stored as enum names.
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value"
            FROM jobs
            WHERE status IN ('Pending', 'Queued')
              AND (scheduled_at IS NULL OR scheduled_at <= {now})
              AND type = ANY({types})
            ORDER BY priority DESC, created_at
            LIMIT {maxJobs}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);

        var claimed = await StartAttemptsAsync(ids, workerId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<ClaimedJob?> ClaimByIdAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default) =>
        (await ClaimByIdsAsync([jobId], workerId, cancellationToken)).SingleOrDefault();

    public async Task<IReadOnlyList<ClaimedJob>> ClaimByIdsAsync(
        IReadOnlyCollection<Guid> jobIds, string workerId, CancellationToken cancellationToken = default)
    {
        if (jobIds.Count == 0)
            return [];

        var now = time.GetUtcNow();
        var requested = jobIds.ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Rows are locked in ID order, so two workers claiming overlapping batches (a redelivered
        // message) can't deadlock: one waits for the other, then finds the job already running.
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value"
            FROM jobs
            WHERE id = ANY({requested})
              AND status IN ('Pending', 'Queued')
              AND (scheduled_at IS NULL OR scheduled_at <= {now})
            ORDER BY id
            FOR UPDATE
            """).ToListAsync(cancellationToken);

        var claimed = await StartAttemptsAsync(ids, workerId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    /// <summary>Opens an attempt for each locked job, in the given order, and saves.</summary>
    private async Task<List<ClaimedJob>> StartAttemptsAsync(
        List<Guid> ids, string workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return [];

        // The rows are locked by the caller's transaction, so loading them through EF is race-free.
        var jobs = await db.Jobs
            .Include(j => j.Attempts)
            .Where(j => ids.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, cancellationToken);

        var types = jobs.Values.Select(j => j.Type).Distinct().ToList();
        var timeouts = await db.JobTypes.AsNoTracking()
            .Where(t => types.Contains(t.Type))
            .ToDictionaryAsync(t => t.Type, t => t.Timeout, cancellationToken);

        var claimed = new List<ClaimedJob>(ids.Count);
        foreach (var id in ids) // preserve priority order
        {
            var job = jobs[id];
            var attempt = job.Start(workerId, now);
            claimed.Add(new ClaimedJob(job.Id, job.Type, job.Payload, attempt.AttemptNumber,
                timeouts.GetValueOrDefault(job.Type, DefaultTimeout), job.TraceParent));
        }

        await db.SaveChangesAsync(cancellationToken);
        return claimed;
    }
}

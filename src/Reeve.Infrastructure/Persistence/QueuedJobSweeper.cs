using Microsoft.EntityFrameworkCore;
using Reeve.Application.Abstractions;

namespace Reeve.Infrastructure.Persistence;

/// <summary>
/// Returns jobs that have sat in Queued for too long to Pending, so the dispatcher publishes them
/// again. A healthy system never needs this; it covers a message lost by the broker or a consumer
/// group that stopped consuming a topic. If the original message does turn up, only one claim wins.
/// </summary>
internal sealed class QueuedJobSweeper(ReeveDbContext db, TimeProvider time) : IQueuedJobSweeper
{
    private const int BatchSize = 500;

    public async Task<int> RequeueStaleAsync(TimeSpan queuedFor, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var cutoff = now - queuedFor;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // updated_at is when the job became Queued: nothing else changes a Queued job.
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value"
            FROM jobs
            WHERE status = 'Queued' AND updated_at < {cutoff}
            ORDER BY updated_at
            LIMIT {BatchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (ids.Count == 0)
            return 0;

        var jobs = await db.Jobs.Where(j => ids.Contains(j.Id)).ToListAsync(cancellationToken);
        foreach (var job in jobs)
            job.ReturnToPending(now);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return jobs.Count;
    }
}

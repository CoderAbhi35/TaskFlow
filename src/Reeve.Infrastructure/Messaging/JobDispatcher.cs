using Microsoft.EntityFrameworkCore;
using Reeve.Application.Abstractions;
using Reeve.Application.Common;
using Reeve.Application.Telemetry;
using Reeve.Contracts.Messages;
using Reeve.Infrastructure.Persistence;
using ContractJobs = Reeve.Contracts.Jobs;
using DomainJobs = Reeve.Domain.Jobs;

namespace Reeve.Infrastructure.Messaging;

/// <summary>
/// Polling publisher over the jobs table. Rows are locked with SKIP LOCKED for the duration of the
/// publish, so several dispatchers can run side by side without publishing the same job concurrently.
/// </summary>
internal sealed class JobDispatcher(ReeveDbContext db, IJobPublisher publisher, TimeProvider time) : IJobDispatcher
{
    public async Task<int> DispatchReadyJobsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value"
            FROM jobs
            WHERE status = 'Pending'
              AND (scheduled_at IS NULL OR scheduled_at <= {now})
            ORDER BY priority DESC, created_at
            LIMIT {batchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (ids.Count == 0)
            return 0;

        var jobs = await db.Jobs.Where(j => ids.Contains(j.Id)).ToDictionaryAsync(j => j.Id, cancellationToken);
        var outgoing = ids.Select(id => jobs[id]).Select(j => new OutgoingJob(
            new JobDispatchMessage(j.Id, j.Type, EnumMapper.Map<DomainJobs.JobPriority, ContractJobs.JobPriority>(j.Priority), now),
            j.TraceParent)).ToList();

        // If this throws, the transaction rolls back and every job stays Pending for the next pass.
        var published = await publisher.PublishAsync(outgoing, cancellationToken);

        foreach (var id in published)
        {
            jobs[id].MarkQueued(now);
            ReeveTelemetry.JobsDispatched.Add(1, new KeyValuePair<string, object?>(ReeveTelemetry.JobTypeTag, jobs[id].Type));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return published.Count;
    }
}

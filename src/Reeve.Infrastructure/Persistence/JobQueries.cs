using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reeve.Application.Abstractions;
using Reeve.Application.Common;
using Reeve.Application.Jobs;
using Reeve.Application.Workers;
using Reeve.Contracts.Common;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Queues;
using Reeve.Contracts.Workers;
using DomainModel = Reeve.Domain.Jobs;
using DomainWorkers = Reeve.Domain.Workers;

namespace Reeve.Infrastructure.Persistence;

internal sealed partial class JobQueries(
    ReeveDbContext db,
    TimeProvider time,
    IOptions<WorkerHealthOptions> workerHealth) : IJobQueries
{
    private static readonly DomainModel.JobStatus[] BacklogStatuses =
    [
        DomainModel.JobStatus.Pending,
        DomainModel.JobStatus.Queued,
        DomainModel.JobStatus.Running,
        DomainModel.JobStatus.DeadLettered,
    ];

    public async Task<JobResponse?> GetJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, cancellationToken);
        return job?.ToResponse();
    }

    public async Task<IReadOnlyList<JobAttemptResponse>?> GetAttemptsAsync(
        Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!await db.Jobs.AnyAsync(j => j.Id == jobId, cancellationToken))
            return null;

        var attempts = await db.JobAttempts.AsNoTracking()
            .Where(a => a.JobId == jobId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync(cancellationToken);

        return attempts.Select(a => a.ToResponse()).ToList();
    }

    public async Task<PagedResponse<JobSummaryResponse>> SearchJobsAsync(
        JobSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        var query = db.Jobs.AsNoTracking();

        if (criteria.Statuses.Count > 0)
            query = query.Where(j => criteria.Statuses.Contains(j.Status));
        if (criteria.JobType is not null)
            query = query.Where(j => j.Type == criteria.JobType);
        if (criteria.Priority is not null)
            query = query.Where(j => j.Priority == criteria.Priority);
        if (criteria.CreatedAfter is not null)
            query = query.Where(j => j.CreatedAt >= criteria.CreatedAfter);
        if (criteria.CreatedBefore is not null)
            query = query.Where(j => j.CreatedAt < criteria.CreatedBefore);
        if (criteria.Before is { } before)
            query = query.Where(j => j.Id.CompareTo(before) < 0);

        // Keyset pagination: fetch one extra row to learn whether another page exists.
        var rows = await query
            .OrderByDescending(j => j.Id)
            .Take(criteria.Limit + 1)
            .Select(j => new
            {
                j.Id, j.Type, j.Status, j.Priority, j.AttemptCount, j.LastError,
                j.ScheduledAt, j.CreatedAt, j.UpdatedAt, j.CompletedAt,
            })
            .ToListAsync(cancellationToken);

        var page = rows.Take(criteria.Limit).Select(j => new JobSummaryResponse(
            j.Id,
            j.Type,
            EnumMapper.Map<DomainModel.JobStatus, JobStatus>(j.Status),
            EnumMapper.Map<DomainModel.JobPriority, JobPriority>(j.Priority),
            j.AttemptCount,
            j.LastError,
            j.ScheduledAt,
            j.CreatedAt,
            j.UpdatedAt,
            j.CompletedAt)).ToList();

        var nextCursor = rows.Count > criteria.Limit ? JobSearchCriteria.EncodeCursor(page[^1].Id) : null;
        return new PagedResponse<JobSummaryResponse>(page, nextCursor);
    }

    public async Task<IReadOnlyList<WorkerResponse>> GetWorkersAsync(CancellationToken cancellationToken = default)
    {
        var workers = await db.Workers.AsNoTracking().OrderBy(w => w.Id).ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        var timeout = workerHealth.Value.HeartbeatTimeout;

        return workers.Select(w => new WorkerResponse(
            w.Id,
            w.Hostname,
            EnumMapper.Map<DomainWorkers.WorkerStatus, WorkerStatus>(w.Status),
            w.Concurrency,
            w.SupportedJobTypes,
            w.RegisteredAt,
            w.LastHeartbeatAt,
            Math.Max(0, (now - w.LastHeartbeatAt).TotalSeconds),
            w.IsStale(now, timeout))).ToList();
    }

    public async Task<IReadOnlyList<QueueStatsResponse>> GetQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();

        // One grouped scan over the active part of the table; terminal jobs other than
        // dead-lettered ones are history, not backlog, and are excluded.
        var backlog = await db.Jobs.AsNoTracking()
            .Where(j => BacklogStatuses.Contains(j.Status))
            .GroupBy(j => j.Type)
            .Select(g => new
            {
                Type = g.Key,
                Ready = g.Count(j => j.Status == DomainModel.JobStatus.Pending
                    && (j.ScheduledAt == null || j.ScheduledAt <= now)),
                Scheduled = g.Count(j => j.Status == DomainModel.JobStatus.Pending && j.ScheduledAt > now),
                Queued = g.Count(j => j.Status == DomainModel.JobStatus.Queued),
                Running = g.Count(j => j.Status == DomainModel.JobStatus.Running),
                DeadLettered = g.Count(j => j.Status == DomainModel.JobStatus.DeadLettered),
                // Queued jobs are waiting too: with Kafka, the dispatcher queues ready jobs within a
                // second whether or not any worker is consuming, so counting only Pending would hide
                // a stalled queue.
                OldestWaitingSince = g
                    .Where(j => (j.Status == DomainModel.JobStatus.Pending && (j.ScheduledAt == null || j.ScheduledAt <= now))
                        || j.Status == DomainModel.JobStatus.Queued)
                    .Min(j => (DateTimeOffset?)(j.ScheduledAt ?? j.CreatedAt)),
            })
            .ToDictionaryAsync(x => x.Type, cancellationToken);

        var definitions = await db.JobTypes.AsNoTracking()
            .ToDictionaryAsync(t => t.Type, t => t.Enabled, cancellationToken);

        return definitions.Keys.Union(backlog.Keys)
            .Order(StringComparer.Ordinal)
            .Select(type =>
            {
                backlog.TryGetValue(type, out var b);
                return new QueueStatsResponse(
                    type,
                    definitions.GetValueOrDefault(type),
                    b?.Ready ?? 0,
                    b?.Scheduled ?? 0,
                    b?.Queued ?? 0,
                    b?.Running ?? 0,
                    b?.DeadLettered ?? 0,
                    b?.OldestWaitingSince is { } since ? Math.Max(0, (now - since).TotalSeconds) : null);
            })
            .ToList();
    }
}

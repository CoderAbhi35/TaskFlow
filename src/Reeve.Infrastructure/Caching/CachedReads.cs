using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Reeve.Application.Abstractions;
using Reeve.Application.Jobs;
using Reeve.Contracts.Common;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Queues;
using Reeve.Contracts.Stats;
using Reeve.Contracts.Workers;
using Reeve.Domain.Jobs;

namespace Reeve.Infrastructure.Caching;

/// <summary>
/// Caches the dashboard's polling endpoints for a couple of seconds. Queue statistics aggregate over
/// the jobs table; with N dashboards open, the database now sees one such query per interval instead
/// of N, and HybridCache collapses concurrent misses into a single query (stampede protection).
/// Everything else passes straight through: job details must never be stale.
/// </summary>
internal sealed class CachedJobQueries(IJobQueries inner, HybridCache cache, IOptions<RedisOptions> options) : IJobQueries
{
    private HybridCacheEntryOptions DashboardEntry => new()
    {
        Expiration = TimeSpan.FromSeconds(options.Value.DashboardCacheSeconds),
        LocalCacheExpiration = TimeSpan.FromSeconds(options.Value.DashboardCacheSeconds),
    };

    public Task<JobResponse?> GetJobAsync(Guid id, CancellationToken cancellationToken = default) =>
        inner.GetJobAsync(id, cancellationToken);

    public Task<IReadOnlyList<JobAttemptResponse>?> GetAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        inner.GetAttemptsAsync(jobId, cancellationToken);

    public Task<PagedResponse<JobSummaryResponse>> SearchJobsAsync(JobSearchCriteria criteria, CancellationToken cancellationToken = default) =>
        inner.SearchJobsAsync(criteria, cancellationToken);

    private bool Enabled => options.Value.DashboardCacheSeconds > 0;

    public async Task<IReadOnlyList<WorkerResponse>> GetWorkersAsync(CancellationToken cancellationToken = default) =>
        !Enabled
            ? await inner.GetWorkersAsync(cancellationToken)
            : await cache.GetOrCreateAsync("dashboard:workers",
                async ct => (List<WorkerResponse>)[.. await inner.GetWorkersAsync(ct)],
                DashboardEntry, cancellationToken: cancellationToken);

    public async Task<OverviewResponse> GetOverviewAsync(TimeSpan window, CancellationToken cancellationToken = default) =>
        !Enabled
            ? await inner.GetOverviewAsync(window, cancellationToken)
            : await cache.GetOrCreateAsync($"dashboard:overview:{(int)window.TotalMinutes}",
                async ct => await inner.GetOverviewAsync(window, ct),
                DashboardEntry, cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<QueueStatsResponse>> GetQueueStatsAsync(CancellationToken cancellationToken = default) =>
        !Enabled
            ? await inner.GetQueueStatsAsync(cancellationToken)
            : await cache.GetOrCreateAsync("dashboard:queues",
                async ct => (List<QueueStatsResponse>)[.. await inner.GetQueueStatsAsync(ct)],
                DashboardEntry, cancellationToken: cancellationToken);
}

/// <summary>
/// Caches job type definitions, which every job submission reads. They change rarely; the trade-off
/// is that disabling a type takes up to <see cref="RedisOptions.JobTypeCacheSeconds"/> to apply.
/// </summary>
internal sealed class CachedJobTypeRepository(IJobTypeRepository inner, HybridCache cache, IOptions<RedisOptions> options)
    : IJobTypeRepository
{
    public async Task<JobTypeDefinition?> GetAsync(string type, CancellationToken cancellationToken = default)
    {
        var snapshot = await cache.GetOrCreateAsync($"job-type:{type}", async ct =>
            await inner.GetAsync(type, ct) is { } definition
                ? new JobTypeSnapshot(definition.Type, definition.TimeoutSeconds, definition.MaxRetries, definition.Enabled)
                : null,
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(options.Value.JobTypeCacheSeconds) },
            cancellationToken: cancellationToken);

        return snapshot is null
            ? null
            : new JobTypeDefinition(snapshot.Type, snapshot.TimeoutSeconds, snapshot.MaxRetries, snapshot.Enabled);
    }

    /// <summary>Serializable copy; the domain entity has private setters.</summary>
    internal sealed record JobTypeSnapshot(string Type, int TimeoutSeconds, int MaxRetries, bool Enabled);
}

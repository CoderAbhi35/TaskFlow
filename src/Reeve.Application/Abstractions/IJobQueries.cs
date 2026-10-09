using Reeve.Application.Jobs;
using Reeve.Contracts.Common;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Queues;
using Reeve.Contracts.Stats;
using Reeve.Contracts.Workers;

namespace Reeve.Application.Abstractions;

/// <summary>
/// Read side: untracked projections straight to response shapes. Commands go through the
/// aggregate and <see cref="IJobRepository"/>; reads don't need to.
/// </summary>
public interface IJobQueries
{
    Task<JobResponse?> GetJobAsync(Guid id, CancellationToken cancellationToken = default);

    /// <returns>Null if the job does not exist.</returns>
    Task<IReadOnlyList<JobAttemptResponse>?> GetAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<PagedResponse<JobSummaryResponse>> SearchJobsAsync(JobSearchCriteria criteria, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkerResponse>> GetWorkersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueueStatsResponse>> GetQueueStatsAsync(CancellationToken cancellationToken = default);

    Task<OverviewResponse> GetOverviewAsync(TimeSpan window, CancellationToken cancellationToken = default);
}

namespace Reeve.Application.Abstractions;

/// <summary>A job this worker now owns: its attempt has been opened and committed.</summary>
/// <param name="TraceParent">Trace to continue while executing: from the transport message if it carried one, else from the job.</param>
public sealed record ClaimedJob(Guid JobId, string JobType, string Payload, int AttemptNumber, TimeSpan Timeout, string? TraceParent = null);

public interface IJobClaimer
{
    /// <summary>
    /// Atomically claims up to <paramref name="maxJobs"/> ready jobs of the given types, highest
    /// priority and oldest first, and starts an attempt for each. Concurrent callers never receive
    /// the same job.
    /// </summary>
    Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string workerId, IReadOnlyCollection<string> jobTypes, int maxJobs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims one specific job, as named by a transport message. Returns null when the job is not
    /// claimable (already running or finished, cancelled, not due yet, or unknown), which is how
    /// duplicate and stale messages are made harmless.
    /// </summary>
    Task<ClaimedJob?> ClaimByIdAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims several specific jobs in one transaction, as named by a batch of transport messages.
    /// Jobs that are not claimable are left out, as in <see cref="ClaimByIdAsync"/>.
    /// </summary>
    Task<IReadOnlyList<ClaimedJob>> ClaimByIdsAsync(IReadOnlyCollection<Guid> jobIds, string workerId, CancellationToken cancellationToken = default);
}

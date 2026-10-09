namespace Reeve.Domain.Jobs;

public sealed class InvalidJobStateTransitionException(Guid jobId, JobStatus from, string action)
    : DomainException($"Job {jobId} cannot {action} while in status {from}.")
{
    public Guid JobId { get; } = jobId;
    public JobStatus From { get; } = from;
}

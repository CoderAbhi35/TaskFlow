namespace Reeve.Application.Execution;

/// <summary>
/// The attempt running this code is no longer the job's open attempt: it was cancelled, or recovery
/// closed it and the job was retried. Its side effects are skipped and its result is discarded.
/// </summary>
public sealed class AttemptSupersededException(Guid jobId, int attemptNumber)
    : Exception($"Attempt {attemptNumber} of job {jobId} was cancelled or replaced; its side effects are skipped.")
{
    public Guid JobId { get; } = jobId;
    public int AttemptNumber { get; } = attemptNumber;
}

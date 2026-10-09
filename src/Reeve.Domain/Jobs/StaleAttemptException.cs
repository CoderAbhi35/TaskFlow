namespace Reeve.Domain.Jobs;

/// <summary>
/// A worker reported on an attempt that is no longer the job's current one, typically because the
/// attempt was recovered after the worker missed heartbeats and the job was restarted.
/// </summary>
public sealed class StaleAttemptException(Guid jobId, int reportedAttempt, int currentAttempt)
    : DomainException($"Job {jobId} is running attempt {currentAttempt}; the report for attempt {reportedAttempt} is stale.")
{
    public int ReportedAttempt { get; } = reportedAttempt;
    public int CurrentAttempt { get; } = currentAttempt;
}

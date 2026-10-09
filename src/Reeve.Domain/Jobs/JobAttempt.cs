namespace Reeve.Domain.Jobs;

public enum JobAttemptStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>One execution of a job by one worker. Created and closed only through <see cref="Job"/>.</summary>
public sealed class JobAttempt
{
    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public int AttemptNumber { get; private set; }
    public string WorkerId { get; private set; } = null!;
    public JobAttemptStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string? Error { get; private set; }

    public TimeSpan? Duration => EndedAt - StartedAt;

    private JobAttempt() { } // EF Core

    internal JobAttempt(Guid jobId, int attemptNumber, string workerId, DateTimeOffset startedAt)
    {
        Id = Guid.CreateVersion7(startedAt);
        JobId = jobId;
        AttemptNumber = attemptNumber;
        WorkerId = workerId;
        Status = JobAttemptStatus.Running;
        StartedAt = startedAt;
    }

    internal void Close(JobAttemptStatus status, DateTimeOffset endedAt, string? error = null)
    {
        Status = status;
        EndedAt = endedAt;
        Error = error;
    }
}

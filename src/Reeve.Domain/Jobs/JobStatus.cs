namespace Reeve.Domain.Jobs;

public enum JobStatus
{
    /// <summary>Persisted and waiting to be run, either immediately or at <see cref="Job.ScheduledAt"/>.</summary>
    Pending,

    /// <summary>Handed to the transport (e.g. published to Kafka) but not yet picked up by a worker.</summary>
    Queued,

    Running,
    Succeeded,

    /// <summary>Terminal failure that retrying cannot fix (validation or business error).</summary>
    Failed,

    Cancelled,

    /// <summary>Transient failures exhausted the retry policy. Parked for operator inspection or manual retry.</summary>
    DeadLettered,
}

public static class JobStatusExtensions
{
    public static bool IsTerminal(this JobStatus status) => status is
        JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled or JobStatus.DeadLettered;
}

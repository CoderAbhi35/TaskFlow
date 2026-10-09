namespace Reeve.Contracts.Jobs;

// API-facing mirrors of the domain enums, serialised as SNAKE_UPPER strings (e.g. "DEAD_LETTERED").
// Kept separate from the domain so internal renames never silently change the public contract.

public enum JobStatus
{
    Pending,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    DeadLettered,
}

public enum JobPriority
{
    Low,
    Normal,
    High,
    Critical,
}

public enum JobAttemptStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

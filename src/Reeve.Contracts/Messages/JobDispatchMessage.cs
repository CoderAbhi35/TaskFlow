using Reeve.Contracts.Jobs;

namespace Reeve.Contracts.Messages;

/// <summary>
/// Published to Kafka when a job is ready to run. It is a notification ("job X is ready"), not a copy
/// of the job: PostgreSQL stays the source of truth, and the worker claims the job there. That makes
/// duplicate or stale messages harmless.
/// </summary>
public sealed record JobDispatchMessage(
    Guid JobId,
    string JobType,
    JobPriority Priority,
    DateTimeOffset DispatchedAt)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Lets consumers reject or adapt to message shapes they don't understand.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
}

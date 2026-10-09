namespace Reeve.Application.Workers;

public sealed class WorkerHealthOptions
{
    public const string SectionName = "Workers";

    /// <summary>A worker with no heartbeat for this long is reported as stale.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 30;

    public TimeSpan HeartbeatTimeout => TimeSpan.FromSeconds(HeartbeatTimeoutSeconds);
}

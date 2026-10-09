namespace Reeve.Worker;

/// <summary>Runtime settings for one worker process (configuration section <c>Worker</c>).</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>How this worker receives work. Both can run side by side against the same database.</summary>
    public WorkerTransport Transport { get; set; } = WorkerTransport.Kafka;

    /// <summary>Maximum jobs this process executes at the same time.</summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>How long to wait before polling again when no work was found.</summary>
    public int PollIntervalMs { get; set; } = 1000;

    public int HeartbeatIntervalSeconds { get; set; } = 10;

    /// <summary>How often this worker looks for jobs abandoned by crashed workers.</summary>
    public int RecoveryIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// On shutdown, in-flight jobs get this long to finish. Jobs still running afterwards are cancelled
    /// and recorded as transient failures, so another worker retries them.
    /// </summary>
    public int ShutdownGraceSeconds { get; set; } = 20;

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMs);
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatIntervalSeconds);
    public TimeSpan RecoveryInterval => TimeSpan.FromSeconds(RecoveryIntervalSeconds);
    public TimeSpan ShutdownGrace => TimeSpan.FromSeconds(ShutdownGraceSeconds);
}

public enum WorkerTransport
{
    /// <summary>Consume dispatch messages from Kafka (requires the scheduler to be running).</summary>
    Kafka,

    /// <summary>Poll PostgreSQL with SKIP LOCKED; needs no broker.</summary>
    Database,
}

/// <summary>This process's identity in the worker registry and on every attempt it runs.</summary>
public sealed class WorkerIdentity
{
    public string Id { get; } =
        $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}".ToLowerInvariant();

    public string Hostname { get; } = Environment.MachineName;
}

namespace Reeve.Domain.Workers;

public enum WorkerStatus
{
    Active,

    /// <summary>Shutting down: finishing in-flight jobs but not accepting new ones.</summary>
    Draining,

    Offline,
}

/// <summary>Registry entry for a worker process, kept alive by periodic heartbeats.</summary>
public sealed class WorkerNode
{
    public const int MaxIdLength = 200;

    public string Id { get; private set; } = null!;
    public string Hostname { get; private set; } = null!;
    public WorkerStatus Status { get; private set; }
    public int Concurrency { get; private set; }
    public List<string> SupportedJobTypes { get; private set; } = [];
    public DateTimeOffset RegisteredAt { get; private set; }
    public DateTimeOffset LastHeartbeatAt { get; private set; }

    private WorkerNode() { } // EF Core

    public static WorkerNode Register(
        string id, string hostname, int concurrency, IEnumerable<string> supportedJobTypes, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength)
            throw new DomainException($"Worker ID must be 1-{MaxIdLength} characters.");
        if (string.IsNullOrWhiteSpace(hostname))
            throw new DomainException("Worker hostname is required.");
        if (concurrency < 1)
            throw new DomainException("Worker concurrency must be at least 1.");

        var types = supportedJobTypes.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        if (types.Count == 0)
            throw new DomainException("A worker must support at least one job type.");

        now = now.ToUniversalTime();
        return new WorkerNode
        {
            Id = id,
            Hostname = hostname,
            Status = WorkerStatus.Active,
            Concurrency = concurrency,
            SupportedJobTypes = types,
            RegisteredAt = now,
            LastHeartbeatAt = now,
        };
    }

    /// <summary>Records liveness. A heartbeat from an offline worker brings it back to Active.</summary>
    public void Heartbeat(DateTimeOffset now)
    {
        LastHeartbeatAt = now.ToUniversalTime();
        if (Status == WorkerStatus.Offline)
            Status = WorkerStatus.Active;
    }

    public void BeginDraining() => Status = WorkerStatus.Draining;

    public void MarkOffline() => Status = WorkerStatus.Offline;

    public bool IsStale(DateTimeOffset now, TimeSpan heartbeatTimeout) =>
        now - LastHeartbeatAt > heartbeatTimeout;

    public bool CanAccept(string jobType) =>
        Status == WorkerStatus.Active && SupportedJobTypes.Contains(jobType);
}

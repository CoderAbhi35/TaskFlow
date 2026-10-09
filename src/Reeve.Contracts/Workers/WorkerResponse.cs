namespace Reeve.Contracts.Workers;

public enum WorkerStatus
{
    Active,
    Draining,
    Offline,
}

public sealed record WorkerResponse(
    string Id,
    string Hostname,
    WorkerStatus Status,
    int Concurrency,
    IReadOnlyList<string> SupportedJobTypes,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastHeartbeatAt,
    double HeartbeatAgeSeconds,
    bool IsStale);

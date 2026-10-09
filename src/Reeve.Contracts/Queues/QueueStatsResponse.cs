namespace Reeve.Contracts.Queues;

/// <summary>Backlog for one job type, computed from the authoritative job table.</summary>
/// <param name="Ready">Pending and due now, not yet handed to the transport.</param>
/// <param name="Scheduled">Pending but not due yet (delayed jobs and retry backoff).</param>
/// <param name="Queued">Handed to the transport (published to Kafka) but not yet claimed by a worker.
/// With Kafka, ready jobs move here within a second, so waiting work is Ready + Queued.</param>
/// <param name="OldestWaitingAgeSeconds">How long the oldest job that is due but not yet picked up
/// (ready or queued) has been waiting.</param>
public sealed record QueueStatsResponse(
    string JobType,
    bool Enabled,
    int Ready,
    int Scheduled,
    int Queued,
    int Running,
    int DeadLettered,
    double? OldestWaitingAgeSeconds);

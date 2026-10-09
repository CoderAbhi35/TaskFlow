namespace Reeve.Contracts.Stats;

/// <summary>
/// The dashboard's headline numbers: is the system healthy, what is slow or failing, how much is waiting.
/// Rates and durations cover the last <see cref="WindowMinutes"/>; backlog and workers are current.
/// </summary>
/// <param name="FailedAttempts">Attempts that failed in the window, including ones that were then retried.</param>
/// <param name="SuccessRate">Succeeded ÷ finished (succeeded, failed, dead-lettered) in the window; null if nothing finished.</param>
/// <param name="ThroughputPerMinute">Jobs finished per minute over the window.</param>
/// <param name="P50DurationMs">Median execution time of successful attempts in the window.</param>
/// <param name="P95DurationMs">95th percentile execution time of successful attempts in the window.</param>
public sealed record OverviewResponse(
    int WindowMinutes,
    DateTimeOffset GeneratedAt,
    int Submitted,
    int Succeeded,
    int Failed,
    int DeadLettered,
    int Cancelled,
    int FailedAttempts,
    double? SuccessRate,
    double ThroughputPerMinute,
    double? P50DurationMs,
    double? P95DurationMs,
    BacklogSummary Backlog,
    WorkerSummary Workers,
    int BucketMinutes,
    IReadOnlyList<ThroughputBucket> Series);

public sealed record BacklogSummary(int Ready, int Scheduled, int Queued, int Running, int DeadLettered);

public sealed record WorkerSummary(int Active, int Draining, int Stale, int Offline, int TotalConcurrency);

/// <summary>Jobs that finished in one time bucket, by outcome.</summary>
public sealed record ThroughputBucket(DateTimeOffset Start, int Succeeded, int Failed);

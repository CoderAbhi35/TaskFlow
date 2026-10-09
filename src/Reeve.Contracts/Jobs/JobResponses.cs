using System.Text.Json;

namespace Reeve.Contracts.Jobs;

public sealed record JobResponse(
    Guid Id,
    string JobType,
    JobStatus Status,
    JobPriority Priority,
    JsonElement Payload,
    RetryPolicyDto RetryPolicy,
    int AttemptCount,
    int RetryCount,
    string? IdempotencyKey,
    string? LastError,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>List item: the job without its payload.</summary>
public sealed record JobSummaryResponse(
    Guid Id,
    string JobType,
    JobStatus Status,
    JobPriority Priority,
    int AttemptCount,
    string? LastError,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record JobAttemptResponse(
    Guid Id,
    int AttemptNumber,
    string WorkerId,
    JobAttemptStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    double? DurationMs,
    string? Error);

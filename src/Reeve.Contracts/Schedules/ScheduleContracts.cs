using System.Text.Json;
using Reeve.Contracts.Jobs;

namespace Reeve.Contracts.Schedules;

/// <param name="Name">Unique, human-readable name, e.g. "Nightly customer reports".</param>
/// <param name="CronExpression">Standard 5-field cron, e.g. <c>0 2 * * *</c> for 02:00 every day.</param>
/// <param name="TimeZone">IANA time zone the expression is evaluated in. Defaults to UTC.</param>
public sealed record CreateScheduleRequest(
    string? Name,
    string? JobType,
    string? CronExpression,
    string? TimeZone = null,
    JobPriority? Priority = null,
    JsonElement? Payload = null,
    RetryPolicyDto? RetryPolicy = null);

public sealed record ScheduleResponse(
    Guid Id,
    string Name,
    string JobType,
    string CronExpression,
    string TimeZone,
    bool Enabled,
    JobPriority Priority,
    JsonElement Payload,
    RetryPolicyDto RetryPolicy,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    Guid? LastJobId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

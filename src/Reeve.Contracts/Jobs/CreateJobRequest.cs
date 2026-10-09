using System.Text.Json;

namespace Reeve.Contracts.Jobs;

/// <param name="JobType">A registered, enabled job type, e.g. <c>GENERATE_REPORT</c>.</param>
/// <param name="Priority">Defaults to <see cref="JobPriority.Normal"/>.</param>
/// <param name="Payload">A JSON object passed to the job handler. Treated as untrusted input.</param>
/// <param name="RetryPolicy">Defaults to the job type's policy.</param>
/// <param name="ScheduledAt">Earliest time the job may run. Omit to run as soon as possible.</param>
public sealed record CreateJobRequest(
    string? JobType,
    JobPriority? Priority = null,
    JsonElement? Payload = null,
    RetryPolicyDto? RetryPolicy = null,
    DateTimeOffset? ScheduledAt = null);

public sealed record RetryPolicyDto(int MaxRetries, int BackoffSeconds);

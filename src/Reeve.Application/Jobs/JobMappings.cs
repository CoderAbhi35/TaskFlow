using System.Text.Json;
using Reeve.Application.Common;
using Reeve.Contracts.Jobs;
using DomainModel = Reeve.Domain.Jobs;

namespace Reeve.Application.Jobs;

public static class JobMappings
{
    public static JobResponse ToResponse(this DomainModel.Job job) => new(
        job.Id,
        job.Type,
        EnumMapper.Map<DomainModel.JobStatus, JobStatus>(job.Status),
        EnumMapper.Map<DomainModel.JobPriority, JobPriority>(job.Priority),
        ParsePayload(job.Payload),
        new RetryPolicyDto(job.MaxRetries, job.BackoffSeconds),
        job.AttemptCount,
        job.RetryCount,
        job.IdempotencyKey,
        job.LastError,
        job.ScheduledAt,
        job.CreatedAt,
        job.UpdatedAt,
        job.CompletedAt);

    public static JobAttemptResponse ToResponse(this DomainModel.JobAttempt attempt) => new(
        attempt.Id,
        attempt.AttemptNumber,
        attempt.WorkerId,
        EnumMapper.Map<DomainModel.JobAttemptStatus, JobAttemptStatus>(attempt.Status),
        attempt.StartedAt,
        attempt.EndedAt,
        attempt.Duration?.TotalMilliseconds,
        attempt.Error);

    public static JsonElement ParsePayload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }
}

using FluentValidation;
using FluentValidation.Results;
using Reeve.Application.Abstractions;
using Reeve.Application.Common;
using Reeve.Application.Telemetry;
using Reeve.Contracts.Jobs;
using DomainModel = Reeve.Domain.Jobs;

namespace Reeve.Application.Jobs;

public sealed record CreateJobResult(JobResponse Job, bool Replayed);

/// <summary>
/// Validates and persists a new job. With an idempotency key, repeating the same request returns the
/// original job instead of creating another, including when both requests arrive at the same time.
/// Keys belong to the caller: two clients using the same key get separate jobs.
/// </summary>
public sealed class CreateJobHandler(
    IValidator<CreateJobRequest> validator,
    IJobRepository jobs,
    IJobTypeRepository jobTypes,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IRequestContext requestContext,
    TimeProvider time)
{
    public const int DefaultBackoffSeconds = 5;
    public const string IdempotencyKeyField = "Idempotency-Key";

    public async Task<CreateJobResult> HandleAsync(
        CreateJobRequest request, string? idempotencyKey, CancellationToken cancellationToken = default)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken);
        idempotencyKey = NormaliseIdempotencyKey(idempotencyKey);

        var fingerprint = idempotencyKey is null ? null : RequestFingerprint.Compute(request);
        if (idempotencyKey is not null)
        {
            var existing = await jobs.GetByIdempotencyKeyAsync(requestContext.Actor, idempotencyKey, cancellationToken);
            if (existing is not null)
                return Replay(existing, idempotencyKey, fingerprint!);
        }

        var jobType = request.JobType!.Trim();
        var definition = await jobTypes.GetAsync(jobType, cancellationToken);
        if (definition is not { Enabled: true })
        {
            throw new ValidationException([
                new ValidationFailure(nameof(CreateJobRequest.JobType),
                    $"Job type '{jobType}' is not registered or is disabled."),
            ]);
        }

        var retryPolicy = request.RetryPolicy is { } policy
            ? new DomainModel.RetryPolicy(policy.MaxRetries, policy.BackoffSeconds)
            : new DomainModel.RetryPolicy(definition.MaxRetries, DefaultBackoffSeconds);

        var job = DomainModel.Job.Create(
            definition.Type,
            request.Payload?.GetRawText(),
            EnumMapper.Map<JobPriority, DomainModel.JobPriority>(request.Priority ?? JobPriority.Normal),
            retryPolicy,
            time.GetUtcNow(),
            request.ScheduledAt,
            idempotencyKey,
            fingerprint,
            ReeveTelemetry.CurrentTraceParent(),
            requestContext.Actor);

        jobs.Add(job);
        audit.Record(AuditActions.JobCreated, AuditEntities.Job, job.Id.ToString(),
            new { job.Type, Priority = job.Priority.ToString(), ScheduledAt = job.ScheduledAt });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateIdempotencyKeyException)
        {
            // Lost a race with a concurrent request using the same key; the winner's job is the answer.
            var winner = await jobs.GetByIdempotencyKeyAsync(requestContext.Actor, idempotencyKey!, cancellationToken)
                ?? throw new InvalidOperationException("Idempotent job vanished after a duplicate-key conflict.");
            return Replay(winner, idempotencyKey!, fingerprint!);
        }

        ReeveTelemetry.JobsSubmitted.Add(1,
            new(ReeveTelemetry.JobTypeTag, job.Type), new(ReeveTelemetry.SourceTag, "api"));
        return new CreateJobResult(job.ToResponse(), Replayed: false);
    }

    private static CreateJobResult Replay(DomainModel.Job existing, string idempotencyKey, string fingerprint) =>
        existing.IdempotencyFingerprint == fingerprint
            ? new CreateJobResult(existing.ToResponse(), Replayed: true)
            : throw new IdempotencyKeyMismatchException(idempotencyKey);

    private static string? NormaliseIdempotencyKey(string? key)
    {
        if (key is null)
            return null;

        key = key.Trim();
        var valid = key.Length is > 0 and <= DomainModel.Job.MaxIdempotencyKeyLength
            && key.All(c => c is >= '!' and <= '~'); // visible ASCII only

        return valid
            ? key
            : throw new ValidationException([
                new ValidationFailure(IdempotencyKeyField,
                    $"Idempotency key must be 1-{DomainModel.Job.MaxIdempotencyKeyLength} visible ASCII characters."),
            ]);
    }
}

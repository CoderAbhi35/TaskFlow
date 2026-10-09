using FluentValidation;
using FluentValidation.Results;
using Reeve.Application.Abstractions;
using Reeve.Application.Common;
using Reeve.Application.Jobs;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Schedules;
using DomainModel = Reeve.Domain.Jobs;
using Reeve.Domain.Schedules;

namespace Reeve.Application.Schedules;

/// <summary>Create, inspect, pause, resume and delete recurring schedules.</summary>
public sealed class ScheduleHandler(
    IValidator<CreateScheduleRequest> validator,
    IScheduleRepository schedules,
    IJobTypeRepository jobTypes,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    TimeProvider time)
{
    public async Task<ScheduleResponse> CreateAsync(CreateScheduleRequest request, CancellationToken cancellationToken = default)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken);

        var jobType = request.JobType!.Trim();
        var definition = await jobTypes.GetAsync(jobType, cancellationToken);
        if (definition is not { Enabled: true })
            throw Invalid(nameof(CreateScheduleRequest.JobType), $"Job type '{jobType}' is not registered or is disabled.");

        var now = time.GetUtcNow();
        var timeZone = request.TimeZone?.Trim() ?? CronSchedule.DefaultTimeZone;
        var firstRun = CronSchedule.NextAfter(request.CronExpression!, timeZone, now)
            ?? throw Invalid(nameof(CreateScheduleRequest.CronExpression), "The expression has no future occurrences.");

        var retryPolicy = request.RetryPolicy is { } policy
            ? new DomainModel.RetryPolicy(policy.MaxRetries, policy.BackoffSeconds)
            : new DomainModel.RetryPolicy(definition.MaxRetries, CreateJobHandler.DefaultBackoffSeconds);

        var schedule = Schedule.Create(
            request.Name!,
            definition.Type,
            request.Payload?.GetRawText(),
            EnumMapper.Map<JobPriority, DomainModel.JobPriority>(request.Priority ?? JobPriority.Normal),
            retryPolicy,
            request.CronExpression!,
            timeZone,
            firstRun,
            now);

        schedules.Add(schedule);
        audit.Record(AuditActions.ScheduleCreated, AuditEntities.Schedule, schedule.Id.ToString(),
            new { schedule.Name, schedule.JobType, schedule.CronExpression, schedule.TimeZone });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return schedule.ToResponse();
    }

    public async Task<ScheduleResponse> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await LoadAsync(id, cancellationToken)).ToResponse();

    public async Task<IReadOnlyList<ScheduleResponse>> ListAsync(CancellationToken cancellationToken = default) =>
        (await schedules.ListAsync(cancellationToken)).Select(s => s.ToResponse()).ToList();

    /// <summary>Idempotent: pausing a paused schedule changes nothing.</summary>
    public async Task<ScheduleResponse> PauseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var schedule = await LoadAsync(id, cancellationToken);
        if (schedule.Enabled)
        {
            schedule.Pause(time.GetUtcNow());
            audit.Record(AuditActions.SchedulePaused, AuditEntities.Schedule, schedule.Id.ToString(), new { schedule.Name });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        return schedule.ToResponse();
    }

    /// <summary>Idempotent. Occurrences missed while paused are skipped: the next run is computed from now.</summary>
    public async Task<ScheduleResponse> ResumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var schedule = await LoadAsync(id, cancellationToken);
        if (!schedule.Enabled)
        {
            var now = time.GetUtcNow();
            schedule.Resume(CronSchedule.NextAfter(schedule.CronExpression, schedule.TimeZone, now), now);
            audit.Record(AuditActions.ScheduleResumed, AuditEntities.Schedule, schedule.Id.ToString(), new { schedule.Name });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        return schedule.ToResponse();
    }

    /// <summary>Jobs the schedule already created are kept.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var schedule = await LoadAsync(id, cancellationToken);
        schedules.Remove(schedule);
        audit.Record(AuditActions.ScheduleDeleted, AuditEntities.Schedule, schedule.Id.ToString(), new { schedule.Name });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<Schedule> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await schedules.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException("Schedule", id);

    private static ValidationException Invalid(string property, string message) =>
        new([new ValidationFailure(property, message)]);
}

public static class ScheduleMappings
{
    public static ScheduleResponse ToResponse(this Schedule schedule) => new(
        schedule.Id,
        schedule.Name,
        schedule.JobType,
        schedule.CronExpression,
        schedule.TimeZone,
        schedule.Enabled,
        EnumMapper.Map<DomainModel.JobPriority, JobPriority>(schedule.Priority),
        JobMappings.ParsePayload(schedule.Payload),
        new RetryPolicyDto(schedule.MaxRetries, schedule.BackoffSeconds),
        schedule.NextRunAt,
        schedule.LastRunAt,
        schedule.LastJobId,
        schedule.CreatedAt,
        schedule.UpdatedAt);
}

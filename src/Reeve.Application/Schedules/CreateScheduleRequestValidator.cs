using System.Text;
using System.Text.Json;
using FluentValidation;
using Reeve.Application.Jobs;
using Reeve.Contracts.Schedules;
using Reeve.Domain.Jobs;
using Reeve.Domain.Schedules;

namespace Reeve.Application.Schedules;

public sealed class CreateScheduleRequestValidator : AbstractValidator<CreateScheduleRequest>
{
    public CreateScheduleRequestValidator()
    {
        RuleFor(r => r.Name)
            .NotEmpty()
            .MaximumLength(Schedule.MaxNameLength)
            .Matches("^[A-Za-z0-9][A-Za-z0-9 _.-]*$")
            .WithMessage("Name may contain only letters, digits, spaces, '_', '.' and '-'.");

        RuleFor(r => r.JobType)
            .NotEmpty()
            .MaximumLength(Job.MaxTypeLength)
            .Matches("^[A-Za-z0-9][A-Za-z0-9_.-]*$")
            .WithMessage("Job type may contain only letters, digits, '_', '.' and '-'.");

        RuleFor(r => r.CronExpression)
            .NotEmpty()
            .MaximumLength(Schedule.MaxCronLength)
            .Must(CronSchedule.IsValidExpression)
            .WithMessage("Cron expression must be a valid 5-field expression, e.g. '0 2 * * *'.");

        RuleFor(r => r.TimeZone)
            .Must(CronSchedule.IsValidTimeZone)
            .WithMessage("Time zone must be a known IANA id, e.g. 'Europe/London' or 'Asia/Kolkata'.")
            .When(r => r.TimeZone is not null);

        RuleFor(r => r.Priority).IsInEnum();

        RuleFor(r => r.Payload!.Value)
            .Must(p => p.ValueKind == JsonValueKind.Object)
            .WithMessage("Payload must be a JSON object.")
            .Must(p => Encoding.UTF8.GetByteCount(p.GetRawText()) <= CreateJobRequestValidator.MaxPayloadBytes)
            .WithMessage($"Payload must be at most {CreateJobRequestValidator.MaxPayloadBytes / 1024} KB.")
            .OverridePropertyName(nameof(CreateScheduleRequest.Payload))
            .When(r => r.Payload.HasValue);

        RuleFor(r => r.RetryPolicy!).ChildRules(policy =>
        {
            policy.RuleFor(p => p.MaxRetries).InclusiveBetween(0, RetryPolicy.MaxAllowedRetries);
            policy.RuleFor(p => p.BackoffSeconds).InclusiveBetween(1, (int)RetryPolicy.MaxDelay.TotalSeconds);
        }).When(r => r.RetryPolicy is not null);
    }
}

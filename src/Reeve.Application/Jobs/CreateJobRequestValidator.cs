using System.Text;
using System.Text.Json;
using FluentValidation;
using Reeve.Contracts.Jobs;
using Reeve.Domain.Jobs;

namespace Reeve.Application.Jobs;

public sealed class CreateJobRequestValidator : AbstractValidator<CreateJobRequest>
{
    /// <summary>Payloads describe work; they are not a place to ship data. Large inputs belong in storage.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    public static readonly TimeSpan MaxScheduleAhead = TimeSpan.FromDays(365);

    public CreateJobRequestValidator(TimeProvider time)
    {
        RuleFor(r => r.JobType)
            .NotEmpty()
            .MaximumLength(Job.MaxTypeLength)
            .Matches("^[A-Za-z0-9][A-Za-z0-9_.-]*$")
            .WithMessage("Job type may contain only letters, digits, '_', '.' and '-'.");

        RuleFor(r => r.Priority).IsInEnum();

        RuleFor(r => r.Payload!.Value)
            .Must(p => p.ValueKind == JsonValueKind.Object)
            .WithMessage("Payload must be a JSON object.")
            .Must(p => Encoding.UTF8.GetByteCount(p.GetRawText()) <= MaxPayloadBytes)
            .WithMessage($"Payload must be at most {MaxPayloadBytes / 1024} KB.")
            .OverridePropertyName(nameof(CreateJobRequest.Payload))
            .When(r => r.Payload.HasValue);

        RuleFor(r => r.RetryPolicy!).ChildRules(policy =>
        {
            policy.RuleFor(p => p.MaxRetries).InclusiveBetween(0, RetryPolicy.MaxAllowedRetries);
            policy.RuleFor(p => p.BackoffSeconds).InclusiveBetween(1, (int)RetryPolicy.MaxDelay.TotalSeconds);
        }).When(r => r.RetryPolicy is not null);

        RuleFor(r => r.ScheduledAt)
            .Must(at => at <= time.GetUtcNow() + MaxScheduleAhead)
            .WithMessage($"Jobs can be scheduled at most {MaxScheduleAhead.TotalDays} days ahead.")
            .When(r => r.ScheduledAt.HasValue);
    }
}

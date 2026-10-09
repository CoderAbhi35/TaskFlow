using System.Text.Json;
using Reeve.Application.Execution;

namespace Reeve.Worker.Handlers;

/// <summary>
/// Base for the sample handlers. They validate their payload like real handlers would, then stand in
/// for the actual work with a delay. An optional <c>simulate</c> block lets a demo or test inject
/// behaviour:
/// <code>
/// "simulate": { "durationMs": 5000, "failure": "transient" | "permanent", "failAttempts": 2 }
/// </code>
/// <c>failAttempts</c> limits the injected failure to the first N attempts, which demonstrates a job
/// that succeeds after retrying.
/// </summary>
public abstract partial class SimulatedJobHandler(ILogger logger) : IJobHandler
{
    public abstract string JobType { get; }

    protected abstract TimeSpan DefaultDuration { get; }

    /// <summary>The job's externally visible effects. Use <see cref="JobExecutionContext.Effects"/> for anything not safe to repeat.</summary>
    protected virtual Task PerformSideEffectsAsync(JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Checks the payload and returns a short description of the work, for logging.</summary>
    /// <exception cref="PermanentJobFailureException">The payload is unusable.</exception>
    protected abstract string Describe(JsonElement payload);

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var description = Describe(context.Payload);
        var simulation = Simulation.From(context.Payload);

        await Task.Delay(simulation.Duration ?? DefaultDuration, cancellationToken);
        await PerformSideEffectsAsync(context, cancellationToken);

        // Injected failures happen after the side effects, the hardest case for retries: the work
        // was done, the job still failed, and the retry must not do it again.
        if (simulation.Failure is { } failure && context.AttemptNumber <= simulation.FailAttempts)
        {
            if (failure == "permanent")
                throw new PermanentJobFailureException($"Simulated permanent failure while {description}.");
            throw new IOException($"Simulated transient failure while {description}.");
        }

        LogCompleted(description);
    }

    protected static int RequireInt(JsonElement payload, string property) =>
        TryGetInt(payload, property, out var number)
            ? number
            : throw new PermanentJobFailureException($"Payload property '{property}' must be an integer.");

    /// <remarks>
    /// Checks the value kind first: <see cref="JsonElement.TryGetInt32"/> throws, rather than returning
    /// false, for non-numbers, and that exception would be misclassified as a transient failure.
    /// </remarks>
    private static bool TryGetInt(JsonElement element, string property, out int number)
    {
        number = 0;
        return element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out number);
    }

    protected static string RequireString(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new PermanentJobFailureException($"Payload property '{property}' must be a non-empty string.");

    protected static string OptionalString(JsonElement payload, string property, string fallback) =>
        payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : fallback;

    [LoggerMessage(LogLevel.Information, "Completed: {Description}")]
    private partial void LogCompleted(string description);

    private sealed record Simulation(TimeSpan? Duration, string? Failure, int FailAttempts)
    {
        public static Simulation From(JsonElement payload)
        {
            if (!payload.TryGetProperty("simulate", out var simulate) || simulate.ValueKind != JsonValueKind.Object)
                return new Simulation(null, null, 0);

            TimeSpan? duration = TryGetInt(simulate, "durationMs", out var ms)
                ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, 600_000))
                : null;

            var failure = simulate.TryGetProperty("failure", out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString()!.ToLowerInvariant()
                : null;
            if (failure is not (null or "transient" or "permanent"))
                throw new PermanentJobFailureException("simulate.failure must be 'transient' or 'permanent'.");

            var failAttempts = TryGetInt(simulate, "failAttempts", out var count) ? count : int.MaxValue;

            return new Simulation(duration, failure, failAttempts);
        }
    }
}

public sealed class GenerateReportHandler(ILogger<GenerateReportHandler> logger) : SimulatedJobHandler(logger)
{
    public override string JobType => "GENERATE_REPORT";
    protected override TimeSpan DefaultDuration => TimeSpan.FromSeconds(2);

    protected override string Describe(JsonElement payload) =>
        $"generating {OptionalString(payload, "reportType", "MONTHLY")} report for customer {RequireInt(payload, "customerId")}";
}

public sealed partial class SendNotificationHandler(ILogger<SendNotificationHandler> logger) : SimulatedJobHandler(logger)
{
    public override string JobType => "SEND_NOTIFICATION";
    protected override TimeSpan DefaultDuration => TimeSpan.FromMilliseconds(300);

    protected override string Describe(JsonElement payload) =>
        $"sending {OptionalString(payload, "template", "GENERIC")} notification to user {RequireInt(payload, "userId")}";

    /// <summary>A user must not get the same notification twice because a later step failed.</summary>
    protected override async Task PerformSideEffectsAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var userId = RequireInt(context.Payload, "userId");
        var delivered = await context.Effects.RunOnceAsync("deliver", _ =>
        {
            // A real provider call would pass context.Effects.IdempotencyKey("deliver") as well.
            LogDelivered(userId, context.AttemptNumber);
            return Task.CompletedTask;
        }, cancellationToken);

        if (!delivered)
            LogAlreadyDelivered(userId, context.AttemptNumber);
    }

    [LoggerMessage(LogLevel.Information, "Notification delivered to user {UserId} (attempt {AttemptNumber})")]
    private partial void LogDelivered(int userId, int attemptNumber);

    [LoggerMessage(LogLevel.Information, "Notification to user {UserId} was already delivered by an earlier attempt; not sending again (attempt {AttemptNumber})")]
    private partial void LogAlreadyDelivered(int userId, int attemptNumber);
}

public sealed class ProcessImageHandler(ILogger<ProcessImageHandler> logger) : SimulatedJobHandler(logger)
{
    public override string JobType => "PROCESS_IMAGE";
    protected override TimeSpan DefaultDuration => TimeSpan.FromSeconds(1);

    protected override string Describe(JsonElement payload) =>
        $"processing image {RequireString(payload, "imageId")}";
}

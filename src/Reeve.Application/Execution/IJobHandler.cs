using System.Text.Json;

namespace Reeve.Application.Execution;

/// <summary>
/// Executes one job type. Handlers are registered in code, so the platform only ever runs work it
/// knows about; a payload is data for a handler, never instructions.
/// </summary>
/// <remarks>
/// Delivery is at-least-once: the same job can be executed again after a worker crash or a timeout.
/// Handlers should make their side effects idempotent, for example by keying them on
/// <see cref="JobExecutionContext.JobId"/>.
/// </remarks>
public interface IJobHandler
{
    string JobType { get; }

    /// <summary>
    /// Completes normally on success. Throw <see cref="PermanentJobFailureException"/> for failures
    /// that retrying cannot fix; any other exception is treated as transient and retried.
    /// </summary>
    Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}

public sealed record JobExecutionContext(Guid JobId, string JobType, int AttemptNumber, JsonElement Payload)
{
    /// <summary>Runs side effects at most once across retries of this job.</summary>
    public JobEffects Effects { get; init; } = JobEffects.None;
}

/// <summary>The job cannot succeed however often it is retried (bad input, missing entity, ...).</summary>
public sealed class PermanentJobFailureException(string message, Exception? innerException = null)
    : Exception(message, innerException);

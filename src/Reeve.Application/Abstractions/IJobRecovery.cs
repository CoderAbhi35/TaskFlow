namespace Reeve.Application.Abstractions;

public sealed record RecoveryResult(int WorkersMarkedOffline, int JobsRecovered);

/// <summary>Detects crashed workers and hands their jobs back to the queue.</summary>
public interface IJobRecovery
{
    /// <summary>
    /// Marks workers without a heartbeat for <paramref name="heartbeatTimeout"/> as offline, then fails
    /// the running attempts they hold as transient failures so the retry policy applies. Safe to run
    /// from every worker at once.
    /// </summary>
    Task<RecoveryResult> RecoverAsync(TimeSpan heartbeatTimeout, CancellationToken cancellationToken = default);
}

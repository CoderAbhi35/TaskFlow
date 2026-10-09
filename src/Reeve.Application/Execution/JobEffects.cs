using Microsoft.Extensions.DependencyInjection;

namespace Reeve.Application.Execution;

/// <summary>Records which named side effects of a job have already been carried out.</summary>
public interface IJobEffectLedger
{
    Task<bool> IsRecordedAsync(Guid jobId, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// True while this attempt is still the job's open attempt: not cancelled, and not closed by
    /// recovery and replaced by another.
    /// </summary>
    Task<bool> IsAttemptCurrentAsync(Guid jobId, int attemptNumber, CancellationToken cancellationToken = default);

    /// <summary>Idempotent: recording an effect twice is not an error.</summary>
    Task RecordAsync(Guid jobId, string key, int attemptNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lets a handler run each side effect at most once across all attempts of a job:
/// <code>
/// await context.Effects.RunOnceAsync("send-email", ct => email.SendAsync(..., ct), ct);
/// </code>
/// If a later step fails and the job is retried, the email is not sent again.
/// </summary>
/// <remarks>
/// <para>
/// Before running an effect it also checks that this attempt is still the job's open attempt. An
/// attempt that was cancelled, or that recovery replaced because its worker stopped heartbeating
/// (while the worker was in fact still running), skips the effect instead of racing the attempt
/// that replaced it.
/// </para>
/// <para>
/// For an external side effect this narrows the duplicate window but cannot close it. If the worker
/// crashes after the effect but before it is recorded, the retry repeats it; and the checks and the
/// effect are not one atomic step. Where the downstream system accepts an idempotency key, also pass
/// <see cref="IdempotencyKey"/> to it; that closes the window. This is at-least-once delivery with
/// deduplication, not exactly-once.
/// </para>
/// </remarks>
public sealed class JobEffects
{
    private readonly IServiceScopeFactory? _scopes;
    private readonly Guid _jobId;
    private readonly int _attemptNumber;

    /// <summary>No ledger: effects always run. Useful for unit-testing handlers.</summary>
    public static JobEffects None { get; } = new(null, Guid.Empty, 0);

    public JobEffects(IServiceScopeFactory? scopes, Guid jobId, int attemptNumber)
    {
        _scopes = scopes;
        _jobId = jobId;
        _attemptNumber = attemptNumber;
    }

    /// <summary>A stable key for this job and effect, to pass to downstream idempotency mechanisms.</summary>
    public string IdempotencyKey(string effectKey) => $"reeve:{_jobId:N}:{effectKey}";

    /// <returns>True if the effect ran now; false if an earlier attempt already did it.</returns>
    /// <exception cref="AttemptSupersededException">This attempt was cancelled or replaced.</exception>
    public async Task<bool> RunOnceAsync(string key, Func<CancellationToken, Task> effect, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_scopes is null)
        {
            await effect(cancellationToken);
            return true;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IJobEffectLedger>();
        if (await ledger.IsRecordedAsync(_jobId, key, cancellationToken))
            return false;
        if (!await ledger.IsAttemptCurrentAsync(_jobId, _attemptNumber, cancellationToken))
            throw new AttemptSupersededException(_jobId, _attemptNumber);

        await effect(cancellationToken);

        // The effect has happened; record it even if the job is being cancelled right now.
        await ledger.RecordAsync(_jobId, key, _attemptNumber, CancellationToken.None);
        return true;
    }
}

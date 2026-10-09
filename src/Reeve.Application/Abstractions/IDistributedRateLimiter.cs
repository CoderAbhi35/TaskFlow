namespace Reeve.Application.Abstractions;

public sealed record RateLimitDecision(bool Allowed, int Limit, int Remaining, TimeSpan RetryAfter);

/// <summary>A rate limiter whose counters are shared by every API instance.</summary>
public interface IDistributedRateLimiter
{
    /// <summary>Whether the shared store can be used right now; callers fall back to a local limiter when not.</summary>
    bool IsAvailable { get; }

    /// <summary>Counts one request against <paramref name="key"/> and says whether it is within the limit.</summary>
    Task<RateLimitDecision> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default);
}

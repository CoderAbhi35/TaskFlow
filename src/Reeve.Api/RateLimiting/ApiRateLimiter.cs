using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Reeve.Application.Abstractions;

namespace Reeve.Api.RateLimiting;

/// <summary>Configuration section <c>RateLimiting</c>.</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public const string DefaultPolicy = "default";
    public const string SubmissionsPolicy = "submissions";
    public const string AuthPolicy = "auth";

    public Dictionary<string, RateLimitPolicy> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [DefaultPolicy] = new() { PermitLimit = 600, WindowSeconds = 60 },
        [SubmissionsPolicy] = new() { PermitLimit = 60, WindowSeconds = 60 },
        // Token requests, per client IP: slows password guessing.
        [AuthPolicy] = new() { PermitLimit = 10, WindowSeconds = 60 },
    };
}

public sealed class RateLimitPolicy
{
    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; }
    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}

/// <summary>
/// Applies a named policy per client. The counters live in Redis so the limit holds across all API
/// instances. When Redis is unavailable the limiter degrades rather than failing: each instance
/// enforces the same limit locally. Clients are still protected, but N instances allow up to N times
/// the limit in total until Redis is back. Rejecting all traffic (fail closed) would turn an optional
/// dependency into an outage; allowing all traffic (fail open) would remove protection exactly when
/// the system is already struggling.
/// </summary>
public sealed partial class ApiRateLimiter(
    IDistributedRateLimiter distributed,
    IOptions<RateLimitingOptions> options,
    TimeProvider time,
    ILogger<ApiRateLimiter> logger) : IDisposable
{
    private static readonly TimeSpan FallbackLogInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, PartitionedRateLimiter<string>> _local = new(StringComparer.OrdinalIgnoreCase);
    private long _lastFallbackLogTicks;

    public async Task<RateLimitDecision> CheckAsync(string policyName, string client, CancellationToken cancellationToken)
    {
        if (!options.Value.Policies.TryGetValue(policyName, out var policy))
            throw new InvalidOperationException($"Rate limit policy '{policyName}' is not configured.");

        if (distributed.IsAvailable)
        {
            try
            {
                return await distributed.TryAcquireAsync($"{policyName}:{client}", policy.PermitLimit, policy.Window, cancellationToken);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                LogFallbackThrottled(ex);
            }
        }
        else
        {
            LogFallbackThrottled(null);
        }

        return AcquireLocally(policyName, policy, client);
    }

    public void Dispose()
    {
        foreach (var limiter in _local.Values)
            limiter.Dispose();
    }

    private RateLimitDecision AcquireLocally(string policyName, RateLimitPolicy policy, string client)
    {
        var limiter = _local.GetOrAdd(policyName, _ => PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                Window = policy.Window,
                QueueLimit = 0,
            })));

        using var lease = limiter.AttemptAcquire(client);
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : policy.Window;
        // Remaining is unknown locally; -1 tells the caller not to advertise it.
        return new RateLimitDecision(lease.IsAcquired, policy.PermitLimit, -1, lease.IsAcquired ? TimeSpan.Zero : retryAfter);
    }

    private void LogFallbackThrottled(Exception? ex)
    {
        var now = time.GetTimestamp();
        var last = Interlocked.Read(ref _lastFallbackLogTicks);
        if (last != 0 && time.GetElapsedTime(last, now) < FallbackLogInterval)
            return;
        if (Interlocked.CompareExchange(ref _lastFallbackLogTicks, now, last) == last)
            LogUsingLocalLimits(ex);
    }

    [LoggerMessage(LogLevel.Warning, "Redis unavailable: enforcing rate limits per instance until it recovers")]
    private partial void LogUsingLocalLimits(Exception? ex);
}

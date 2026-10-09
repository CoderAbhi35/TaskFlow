using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Reeve.Infrastructure.Caching;

/// <summary>
/// Reports Degraded, never Unhealthy, when Redis is unreachable: the API keeps serving (rate limits
/// fall back to per-instance counters, reads go to PostgreSQL), so it should not be taken out of a
/// load balancer or restarted because of Redis.
/// </summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis, IOptions<RedisOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!redis.IsConnected)
            return HealthCheckResult.Degraded("Redis is not connected; using local fallbacks.");

        try
        {
            var latency = await redis.GetDatabase().PingAsync()
                .WaitAsync(TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMs * 4), cancellationToken);
            return HealthCheckResult.Healthy($"Redis ping {latency.TotalMilliseconds:0.#} ms");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return HealthCheckResult.Degraded("Redis did not answer; using local fallbacks.", ex);
        }
    }
}

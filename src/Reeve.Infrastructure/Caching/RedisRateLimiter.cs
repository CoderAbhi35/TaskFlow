using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Reeve.Application.Abstractions;

namespace Reeve.Infrastructure.Caching;

/// <summary>
/// Sliding-window-counter rate limiter in Redis. It keeps one counter per fixed window and estimates
/// the sliding count as <c>previous × (unelapsed fraction) + current</c>: O(1) memory and a single
/// round trip per request, without the burst at window boundaries that a plain fixed window allows.
/// The check and the increment run in one Lua script, so concurrent API instances cannot both take
/// the last permit.
/// </summary>
/// <remarks>Windows are derived from the API instances' clocks, which are assumed to be NTP-synchronised.</remarks>
internal sealed class RedisRateLimiter(IConnectionMultiplexer redis, IOptions<RedisOptions> options, TimeProvider time)
    : IDistributedRateLimiter
{
    private static readonly LuaScript Script = LuaScript.Prepare("""
        local current = tonumber(redis.call('GET', @currentKey) or '0')
        local previous = tonumber(redis.call('GET', @previousKey) or '0')
        local limit = tonumber(@limit)
        local window = tonumber(@windowMs)
        local elapsed = tonumber(@elapsedMs)
        local estimate = previous * ((window - elapsed) / window) + current
        if estimate >= limit then
            local retry = window - elapsed
            if current < limit and previous > 0 then
                retry = window - elapsed - ((limit - current) * window / previous)
            end
            return { 0, math.floor(estimate), math.max(1, math.ceil(retry)) }
        end
        redis.call('INCR', @currentKey)
        redis.call('PEXPIRE', @currentKey, window * 2)
        return { 1, math.ceil(estimate + 1), 0 }
        """);

    public bool IsAvailable => redis.IsConnected;

    public async Task<RateLimitDecision> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var windowMs = (long)window.TotalMilliseconds;
        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        var index = nowMs / windowMs;
        var prefix = $"{options.Value.KeyPrefix}rl:{key}";

        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(Script, new
        {
            currentKey = (RedisKey)$"{prefix}:{index}",
            previousKey = (RedisKey)$"{prefix}:{index - 1}",
            limit,
            windowMs,
            elapsedMs = nowMs - index * windowMs,
        }).WaitAsync(TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMs), cancellationToken))!;

        var allowed = (int)result[0] == 1;
        var used = (int)result[1];
        return new RateLimitDecision(allowed, limit, Math.Max(0, limit - used), TimeSpan.FromMilliseconds((long)result[2]));
    }
}

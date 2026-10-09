using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Reeve.Infrastructure.Caching;

/// <summary>
/// Wraps the Redis distributed cache so an unavailable Redis costs nothing. While the connection is
/// down every call is skipped immediately (a miss for reads, a no-op for writes) and HybridCache
/// carries on with its in-memory layer and the database. Without this, each cache miss waited out
/// the connect timeout, adding over a second to requests during a Redis outage.
/// </summary>
internal sealed class ResilientDistributedCache(IDistributedCache inner, IConnectionMultiplexer redis, IOptions<RedisOptions> options)
    : IDistributedCache
{
    private TimeSpan Timeout => TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMs);

    public byte[]? Get(string key) => redis.IsConnected ? Try(() => inner.Get(key)) : null;

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        redis.IsConnected ? await TryAsync(() => inner.GetAsync(key, token)) : null;

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        if (redis.IsConnected)
            Try(() => { inner.Set(key, value, options); return true; });
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        if (redis.IsConnected)
            await TryAsync(async () => { await inner.SetAsync(key, value, options, token); return true; });
    }

    public void Refresh(string key)
    {
        if (redis.IsConnected)
            Try(() => { inner.Refresh(key); return true; });
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        if (redis.IsConnected)
            await TryAsync(async () => { await inner.RefreshAsync(key, token); return true; });
    }

    public void Remove(string key)
    {
        if (redis.IsConnected)
            Try(() => { inner.Remove(key); return true; });
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        if (redis.IsConnected)
            await TryAsync(async () => { await inner.RemoveAsync(key, token); return true; });
    }

    private static T? Try<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return default;
        }
    }

    private async Task<T?> TryAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().WaitAsync(Timeout);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return default;
        }
    }
}

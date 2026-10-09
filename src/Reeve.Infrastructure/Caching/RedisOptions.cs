namespace Reeve.Infrastructure.Caching;

/// <summary>Configuration section <c>Redis</c>.</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>Prefix for every key Reeve writes, so the instance can be shared safely.</summary>
    public string KeyPrefix { get; set; } = "reeve:";

    /// <summary>
    /// Kept short: Redis only backs optional features, so a slow or absent Redis must cost a request
    /// milliseconds, not seconds, before the caller falls back.
    /// </summary>
    public int OperationTimeoutMs { get; set; } = 250;

    /// <summary>How long queue statistics and the worker list are cached for dashboard polling. 0 disables it.</summary>
    public int DashboardCacheSeconds { get; set; } = 2;

    /// <summary>How long job type definitions are cached. Disabling a type takes up to this long to apply.</summary>
    public int JobTypeCacheSeconds { get; set; } = 30;
}

using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Reeve.Application.Abstractions;
using Reeve.Infrastructure.Persistence;

namespace Reeve.Infrastructure.Caching;

public static class RedisServiceCollectionExtensions
{
    public const string HealthCheckName = "redis";

    /// <summary>
    /// Redis-backed rate limiting and caching for the API. Call after <c>AddReevePersistence</c>:
    /// it wraps the query and job type services with caching decorators.
    /// </summary>
    public static IServiceCollection AddReeveRedis(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));

        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<RedisOptions>>().Value;
            var config = ConfigurationOptions.Parse(settings.ConnectionString);
            // Start even if Redis is down and keep reconnecting in the background: Redis only backs
            // optional features, so it must never stop the API from starting.
            config.AbortOnConnectFail = false;
            config.ConnectTimeout = 1000;
            config.SyncTimeout = config.AsyncTimeout = settings.OperationTimeoutMs;
            return ConnectionMultiplexer.Connect(config);
        });

        services.AddStackExchangeRedisCache(_ => { });
        services.AddOptions<Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions>()
            .Configure<IServiceProvider>((cache, sp) =>
            {
                var settings = sp.GetRequiredService<IOptions<RedisOptions>>().Value;
                cache.InstanceName = $"{settings.KeyPrefix}cache:";
                cache.ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>());
            });
        Decorate<Microsoft.Extensions.Caching.Distributed.IDistributedCache, ResilientDistributedCache>(services);
        services.AddHybridCache();

        services.AddSingleton<IDistributedRateLimiter, RedisRateLimiter>();

        Decorate<IJobQueries, CachedJobQueries>(services);
        Decorate<IJobTypeRepository, CachedJobTypeRepository>(services);

        services.AddHealthChecks().AddCheck<RedisHealthCheck>(HealthCheckName, failureStatus: HealthStatus.Degraded);
        return services;
    }

    /// <summary>Replaces the registration of <typeparamref name="TService"/> with a decorator around it.</summary>
    private static void Decorate<TService, TDecorator>(IServiceCollection services)
        where TService : class
        where TDecorator : class, TService
    {
        var original = services.Last(d => d.ServiceType == typeof(TService));
        services.Remove(original);
        services.Add(ServiceDescriptor.Describe(typeof(TService), sp =>
        {
            var inner = (TService)(original.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!));
            return ActivatorUtilities.CreateInstance<TDecorator>(sp, inner);
        }, original.Lifetime));
    }
}

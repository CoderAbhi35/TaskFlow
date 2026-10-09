using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reeve.Application.Abstractions;
using Reeve.Infrastructure.Health;

namespace Reeve.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Kafka options and topic management; needed by producers and consumers.</summary>
    public static IServiceCollection AddReeveKafka(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.AddSingleton<KafkaTopicManager>();
        services.AddSingleton<KafkaDeadLetterPublisher>();
        return services;
    }

    /// <summary>The publishing side: Kafka producer and the outbox dispatcher.</summary>
    public static IServiceCollection AddReeveDispatcher(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddReeveKafka(configuration);
        services.AddSingleton<IJobPublisher, KafkaJobPublisher>();
        services.AddScoped<IJobDispatcher, JobDispatcher>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LoopMonitor>();
        return services;
    }
}

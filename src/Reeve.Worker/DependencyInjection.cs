using Microsoft.Extensions.DependencyInjection.Extensions;
using Reeve.Application.Execution;
using Reeve.Application.Workers;
using Reeve.Infrastructure.Health;
using Reeve.Infrastructure.Messaging;
using Reeve.Worker.Handlers;
using Reeve.Worker.Services;

namespace Reeve.Worker;

public static class DependencyInjection
{
    /// <summary>
    /// The worker runtime: registration and heartbeats, the configured transport and crash recovery.
    /// Job handlers are registered separately (<see cref="AddSampleJobHandlers"/>), so tests can supply their own.
    /// </summary>
    public static IServiceCollection AddReeveWorker(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(WorkerOptions.SectionName);
        var settings = section.Get<WorkerOptions>() ?? new WorkerOptions();

        services.Configure<WorkerOptions>(section);
        services.Configure<WorkerHealthOptions>(configuration.GetSection(WorkerHealthOptions.SectionName));

        // Leave room after the drain grace period for cancelled jobs to record their outcome.
        services.Configure<HostOptions>(o => o.ShutdownTimeout = settings.ShutdownGrace + TimeSpan.FromSeconds(15));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WorkerIdentity>();
        services.AddSingleton<JobHandlerRegistry>();
        services.AddSingleton<JobExecutor>();
        services.AddSingleton<InFlightJobs>();
        services.TryAddSingleton<LoopMonitor>();

        // Order matters: hosted services start in this order and stop in reverse, so the worker is
        // registered before it takes work and goes offline only after in-flight jobs have drained.
        services.AddHostedService<WorkerLifecycleService>();
        if (settings.Transport == WorkerTransport.Kafka)
        {
            services.AddReeveKafka(configuration);
            services.AddHostedService<KafkaJobConsumerService>();
        }
        else
        {
            services.AddHostedService<JobProcessingService>();
        }
        services.AddHostedService<RecoveryService>();

        return services;
    }

    public static IServiceCollection AddSampleJobHandlers(this IServiceCollection services)
    {
        services.AddSingleton<IJobHandler, GenerateReportHandler>();
        services.AddSingleton<IJobHandler, SendNotificationHandler>();
        services.AddSingleton<IJobHandler, ProcessImageHandler>();
        return services;
    }
}

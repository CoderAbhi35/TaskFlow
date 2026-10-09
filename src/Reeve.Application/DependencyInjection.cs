using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reeve.Application.Jobs;

namespace Reeve.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddReeveApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssemblyContaining<CreateJobRequestValidator>(ServiceLifetime.Singleton);
        services.AddScoped<CreateJobHandler>();
        services.AddScoped<JobLifecycleHandler>();
        services.AddScoped<Schedules.ScheduleHandler>();
        return services;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Reeve.Application.Abstractions;
using Reeve.Application.Execution;
using Reeve.Infrastructure.Persistence;

namespace Reeve.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Reeve";

    /// <summary>
    /// Most connections this process opens (<c>Database:MaxPoolSize</c>). Npgsql's default is 100 per
    /// process, which is PostgreSQL's whole default <c>max_connections</c>: one busy API instance could
    /// take every connection and the next client gets "too many clients" (measured under load). With
    /// a limit, requests wait briefly for a pooled connection instead. Keep the sum over all
    /// processes below the server's limit. An explicit value in the connection string wins.
    /// </summary>
    public const string MaxPoolSizeKey = "Database:MaxPoolSize";

    public static IServiceCollection AddReevePersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured.");
        connectionString = WithPoolLimit(connectionString, configuration.GetValue<int?>(MaxPoolSizeKey));

        services.AddOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<ReeveDbContext>(options => ConfigureDbContext(options, connectionString));
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IJobTypeRepository, JobTypeRepository>();
        services.AddScoped<IJobQueries, JobQueries>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IJobClaimer, JobClaimer>();
        services.AddScoped<IJobRecovery, JobRecovery>();
        services.AddScoped<IWorkerRegistry, WorkerRegistry>();
        services.AddScoped<IScheduleRepository, ScheduleRepository>();
        services.AddScoped<IScheduleRunner, ScheduleRunner>();
        services.AddScoped<IQueuedJobSweeper, QueuedJobSweeper>();
        services.AddScoped<IJobEffectLedger, JobEffectLedger>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IAuditQueries, AuditQueries>();
        // Background processes act as "system"; the API replaces this with the signed-in user.
        services.TryAddScoped<IRequestContext, SystemRequestContext>();

        return services;
    }

    internal static string WithPoolLimit(string connectionString, int? maxPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (maxPoolSize is > 0 && !connectionString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase)
                               && !connectionString.Contains("MaxPoolSize", StringComparison.OrdinalIgnoreCase))
            builder.MaxPoolSize = maxPoolSize.Value;
        return builder.ConnectionString;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();
}

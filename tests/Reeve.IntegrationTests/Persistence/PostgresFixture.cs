using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Application;
using Reeve.Domain.Jobs;
using Reeve.Infrastructure;
using Reeve.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Reeve.IntegrationTests.Persistence;

/// <summary>
/// Starts one disposable PostgreSQL container for the test collection and applies the real
/// migrations to it, so the tests also prove the migrations are valid.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public ServiceProvider Services { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Services = BuildServices();

        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ReeveDbContext>().Database.MigrateAsync();
    }

    /// <summary>Each scope has its own DbContext, like separate HTTP requests or worker processes.</summary>
    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    /// <summary>A fresh container of application services on this database, plus test-specific extras.</summary>
    public ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddReeveApplication()
            .AddReevePersistence(BuildConfiguration());
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public IConfiguration BuildConfiguration(IDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{Reeve.Infrastructure.DependencyInjection.ConnectionStringName}"] = ConnectionString,
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            settings[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>Registers a job type unique to the calling test, so tests never pick up each other's jobs.</summary>
    public async Task<string> RegisterJobTypeAsync(int timeoutSeconds = 60, int maxRetries = 3)
    {
        var type = $"TEST_{Guid.NewGuid():N}";
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReeveDbContext>();
        db.JobTypes.Add(new JobTypeDefinition(type, timeoutSeconds, maxRetries));
        await db.SaveChangesAsync();
        return type;
    }

    public async Task<Job> AddJobAsync(string type, JobPriority priority = JobPriority.Normal,
        RetryPolicy? retryPolicy = null, DateTimeOffset? scheduledAt = null, string? payload = null)
    {
        var job = Job.Create(type, payload, priority, retryPolicy ?? RetryPolicy.Default, DateTimeOffset.UtcNow, scheduledAt);
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReeveDbContext>();
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    public async Task<Job> GetJobAsync(Guid id)
    {
        await using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ReeveDbContext>().Jobs
            .AsNoTracking().Include(j => j.Attempts).SingleAsync(j => j.Id == id);
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using Reeve.Api.Auth;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Serialization;
using Reeve.Domain.Jobs;
using Reeve.Infrastructure;
using Reeve.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Reeve.ApiTests.Infrastructure;

/// <summary>
/// Runs the real API in memory against disposable PostgreSQL and Redis containers, with the real
/// migrations. Rate limits are set high and dashboard caching is off so that ordinary tests are not
/// affected by each other; the Redis-specific tests switch them on with <see cref="WithSettings"/>.
/// </summary>
public sealed class ReeveApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public const string TestIssuer = "reeve-test";
    public const string TestSigningKey = "api-tests-signing-key-0123456789abcdef-0123456789";

    public string RedisConnectionString => _redis.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ReeveDbContext>().Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", _postgres.GetConnectionString());
        builder.UseSetting("Redis:ConnectionString", _redis.GetConnectionString());
        builder.UseSetting("Redis:KeyPrefix", $"test-{Guid.NewGuid():N}:");
        builder.UseSetting("Redis:DashboardCacheSeconds", "0");
        builder.UseSetting("RateLimiting:Policies:default:PermitLimit", "1000000");
        builder.UseSetting("RateLimiting:Policies:submissions:PermitLimit", "1000000");
        builder.UseSetting("RateLimiting:Policies:auth:PermitLimit", "1000000");
        // Keep tracing on (so requests create spans) but send the export nowhere, not to a local collector.
        builder.UseSetting("Telemetry:OtlpEndpoint", "http://localhost:1");
        builder.UseSetting("Telemetry:ExportTimeoutMs", "100");
        builder.UseSetting("Auth:Issuer", TestIssuer);
        builder.UseSetting("Auth:Audience", "reeve-api");
        builder.UseSetting("Auth:SigningKey", TestSigningKey);
        builder.UseSetting("Auth:DevIssuer:Enabled", "true");
        string[] roles = [Roles.Viewer, Roles.Operator, Roles.Admin];
        for (var i = 0; i < roles.Length; i++)
        {
            builder.UseSetting($"Auth:DevIssuer:Users:{i}:Username", roles[i]);
            builder.UseSetting($"Auth:DevIssuer:Users:{i}:Password", $"{roles[i]}-password");
            builder.UseSetting($"Auth:DevIssuer:Users:{i}:Roles:0", roles[i]);
        }
    }

    /// <summary>A second API instance on the same databases with some settings overridden.</summary>
    public WebApplicationFactory<Program> WithSettings(IDictionary<string, string?> settings) =>
        WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });

    /// <summary>Registers a job type unique to the calling test, so tests don't see each other's jobs.</summary>
    public async Task<string> RegisterJobTypeAsync(bool enabled = true, int maxRetries = 3)
    {
        var type = $"TEST_{Guid.NewGuid():N}";
        await WithDbAsync(async db =>
        {
            db.JobTypes.Add(new JobTypeDefinition(type, timeoutSeconds: 60, maxRetries, enabled));
            await db.SaveChangesAsync();
        });
        return type;
    }

    /// <summary>Drives a job through transitions the API does not expose yet (the worker's side).</summary>
    public Task UpdateJobAsync(Guid id, Action<Job> change) => WithDbAsync(async db =>
    {
        var job = await db.Jobs.Include(j => j.Attempts).SingleAsync(j => j.Id == id);
        change(job);
        await db.SaveChangesAsync();
    });

    public async Task WithDbAsync(Func<ReeveDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<ReeveDbContext>());
    }

    public async Task<JobResponse> CreateJobAsync(HttpClient client, CreateJobRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/jobs", request, ReeveJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options))!;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
}

public static class AuthenticatedClients
{
    /// <summary>A client carrying a real JWT for the role, signed with the test key by the API's own token service.</summary>
    public static HttpClient CreateClientAs(this WebApplicationFactory<Program> factory, string role, string? username = null)
    {
        var token = factory.Services.GetRequiredService<TokenService>().Issue(username ?? $"test-{role}", [role]);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ReeveApiFactory>
{
    public const string Name = "api";
}

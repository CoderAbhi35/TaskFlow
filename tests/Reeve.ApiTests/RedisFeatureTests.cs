using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Queues;
using Reeve.Contracts.Serialization;

namespace Reeve.ApiTests;

/// <summary>Rate limiting and caching against a real Redis, including what happens when Redis is gone.</summary>
[Collection(ApiCollection.Name)]
public class RedisFeatureTests(ReeveApiFactory factory)
{
    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/jobs", new CreateJobRequest("SEND_NOTIFICATION"), ReeveJson.Options);

    private WebApplicationFactory<Program> Instance(string keyPrefix, int submissionLimit, string? redis = null, int dashboardCacheSeconds = 0) =>
        factory.WithSettings(new Dictionary<string, string?>
        {
            ["Redis:KeyPrefix"] = keyPrefix,
            ["Redis:ConnectionString"] = redis ?? factory.RedisConnectionString,
            ["Redis:DashboardCacheSeconds"] = dashboardCacheSeconds.ToString(),
            ["RateLimiting:Policies:submissions:PermitLimit"] = submissionLimit.ToString(),
            ["RateLimiting:Policies:submissions:WindowSeconds"] = "60",
        });

    private static string UniquePrefix() => $"test-{Guid.NewGuid():N}:";

    [Fact]
    public async Task Submissions_over_the_limit_get_429_with_retry_after()
    {
        await using var api = Instance(UniquePrefix(), submissionLimit: 3);
        using var client = api.CreateClientAs(Roles.Admin);

        var accepted = new List<HttpResponseMessage>();
        for (var i = 0; i < 3; i++)
            accepted.Add(await SubmitAsync(client));
        var rejected = await SubmitAsync(client);

        accepted.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);
        accepted.Select(r => r.Headers.GetValues("RateLimit-Remaining").Single()).Should().Equal("2", "1", "0");

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);
        rejected.Headers.GetValues("RateLimit-Policy").Should().Equal("submissions");
        var problem = (await rejected.Content.ReadFromJsonAsync<ProblemResponse>(ReeveJson.Options))!;
        problem.Code.Should().Be("rate_limited");
        problem.CorrelationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reads_are_not_limited_by_the_submission_policy()
    {
        await using var api = Instance(UniquePrefix(), submissionLimit: 1);
        using var client = api.CreateClientAs(Roles.Admin);
        await SubmitAsync(client);
        (await SubmitAsync(client)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var read = await client.GetAsync("/api/v1/jobs?limit=1");

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        read.Headers.GetValues("RateLimit-Policy").Should().Equal("default");
    }

    [Fact]
    public async Task The_limit_is_shared_by_all_api_instances()
    {
        var prefix = UniquePrefix();
        await using var instanceA = Instance(prefix, submissionLimit: 4);
        await using var instanceB = Instance(prefix, submissionLimit: 4);
        using var clientA = instanceA.CreateClientAs(Roles.Admin);
        using var clientB = instanceB.CreateClientAs(Roles.Admin);

        var statuses = new List<HttpStatusCode>();
        foreach (var client in new[] { clientA, clientB, clientA, clientB, clientA })
            statuses.Add((await SubmitAsync(client)).StatusCode);

        statuses.Should().Equal(
            HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created,
            HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Without_redis_the_api_degrades_instead_of_failing()
    {
        // Nothing listens on port 1: Redis is "down" for this instance.
        await using var api = Instance(UniquePrefix(), submissionLimit: 2, redis: "localhost:1", dashboardCacheSeconds: 5);
        using var client = api.CreateClientAs(Roles.Admin);

        // Limits are still enforced, per instance.
        (await SubmitAsync(client)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await SubmitAsync(client)).StatusCode.Should().Be(HttpStatusCode.Created);
        var third = await SubmitAsync(client);
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        third.Headers.Contains("RateLimit-Remaining").Should().BeFalse("the local fallback cannot know the shared count");

        // Cached reads fall through to PostgreSQL.
        (await client.GetAsync("/api/v1/queues")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/workers")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Health says Degraded, but still 200: the instance should keep receiving traffic.
        var health = await client.GetAsync("/api/v1/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync()).Should().Be("Degraded");
    }

    [Fact]
    public async Task Queue_statistics_are_served_from_the_shared_cache()
    {
        var prefix = UniquePrefix();
        await using var api = Instance(prefix, submissionLimit: 1000, dashboardCacheSeconds: 30);
        using var client = api.CreateClientAs(Roles.Admin);
        var type = await factory.RegisterJobTypeAsync();

        var before = await client.GetFromJsonAsync<List<QueueStatsResponse>>("/api/v1/queues", ReeveJson.Options);
        await factory.CreateJobAsync(client, new CreateJobRequest(type));
        var after = await client.GetFromJsonAsync<List<QueueStatsResponse>>("/api/v1/queues", ReeveJson.Options);

        before!.Single(q => q.JobType == type).Ready.Should().Be(0);
        after!.Single(q => q.JobType == type).Ready.Should().Be(0, "the cached snapshot is still within its 30 s lifetime");

        // The entry lives in Redis, so every API instance shares one snapshot.
        var redis = api.Services.GetRequiredService<IConnectionMultiplexer>();
        (await redis.GetDatabase().KeyExistsAsync($"{prefix}cache:dashboard:queues")).Should().BeTrue();

        // A fresh instance with caching off sees the real count.
        using var uncached = factory.CreateClientAs(Roles.Admin);
        var live = await uncached.GetFromJsonAsync<List<QueueStatsResponse>>("/api/v1/queues", ReeveJson.Options);
        live!.Single(q => q.JobType == type).Ready.Should().Be(1);
    }
}

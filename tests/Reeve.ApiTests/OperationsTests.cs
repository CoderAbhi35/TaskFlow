using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Queues;
using Reeve.Contracts.Serialization;
using Reeve.Contracts.Workers;
using Reeve.Domain.Workers;

namespace Reeve.ApiTests;

[Collection(ApiCollection.Name)]
public class OperationsTests(ReeveApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClientAs(Roles.Admin);

    [Fact]
    public async Task Queue_stats_break_down_the_backlog_per_job_type()
    {
        var type = await factory.RegisterJobTypeAsync();
        await factory.CreateJobAsync(_client, new CreateJobRequest(type));
        await factory.CreateJobAsync(_client, new CreateJobRequest(type));
        await factory.CreateJobAsync(_client, new CreateJobRequest(type, ScheduledAt: DateTimeOffset.UtcNow.AddHours(1)));
        var running = await factory.CreateJobAsync(_client, new CreateJobRequest(type));
        var deadLettered = await factory.CreateJobAsync(_client, new CreateJobRequest(type,
            RetryPolicy: new RetryPolicyDto(MaxRetries: 0, BackoffSeconds: 1)));
        var succeeded = await factory.CreateJobAsync(_client, new CreateJobRequest(type));

        var now = DateTimeOffset.UtcNow;
        await factory.UpdateJobAsync(running.Id, j => j.Start("w", now));
        await factory.UpdateJobAsync(deadLettered.Id, j =>
        {
            j.Start("w", now);
            j.Fail("timeout", isTransient: true, now, Random.Shared);
        });
        await factory.UpdateJobAsync(succeeded.Id, j =>
        {
            j.Start("w", now);
            j.Succeed(now);
        });

        var stats = (await _client.GetFromJsonAsync<List<QueueStatsResponse>>("/api/v1/queues", ReeveJson.Options))!;

        var queue = stats.Single(q => q.JobType == type);
        queue.Should().BeEquivalentTo(new
        {
            JobType = type, Enabled = true, Ready = 2, Scheduled = 1, Queued = 0, Running = 1, DeadLettered = 1,
        });
        queue.OldestWaitingAgeSeconds.Should().BeGreaterThanOrEqualTo(0);

        stats.Select(q => q.JobType).Should().Contain(["GENERATE_REPORT", "PROCESS_IMAGE", "SEND_NOTIFICATION"]);
    }

    [Fact]
    public async Task Queued_jobs_count_as_waiting()
    {
        // With Kafka, the dispatcher queues ready jobs within a second even when no worker is
        // consuming. A queue holding only Queued jobs is still stalled work, and must look like it.
        var type = await factory.RegisterJobTypeAsync();
        var job = await factory.CreateJobAsync(_client, new CreateJobRequest(type));
        await factory.UpdateJobAsync(job.Id, j => j.MarkQueued(DateTimeOffset.UtcNow));

        var stats = (await _client.GetFromJsonAsync<List<QueueStatsResponse>>("/api/v1/queues", ReeveJson.Options))!;

        var queue = stats.Single(q => q.JobType == type);
        queue.Ready.Should().Be(0);
        queue.Queued.Should().Be(1);
        queue.OldestWaitingAgeSeconds.Should().NotBeNull().And.BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Workers_report_heartbeat_health()
    {
        var healthyId = $"healthy-{Guid.NewGuid():N}";
        var staleId = $"stale-{Guid.NewGuid():N}";
        await factory.WithDbAsync(async db =>
        {
            db.Workers.Add(WorkerNode.Register(healthyId, "host-a", 4, ["GENERATE_REPORT"], DateTimeOffset.UtcNow));
            db.Workers.Add(WorkerNode.Register(staleId, "host-b", 2, ["SEND_NOTIFICATION"], DateTimeOffset.UtcNow.AddMinutes(-5)));
            await db.SaveChangesAsync();
        });

        var workers = await _client.GetFromJsonAsync<List<WorkerResponse>>("/api/v1/workers", ReeveJson.Options);

        var healthy = workers!.Single(w => w.Id == healthyId);
        healthy.IsStale.Should().BeFalse();
        healthy.Status.Should().Be(Contracts.Workers.WorkerStatus.Active);
        healthy.SupportedJobTypes.Should().Equal("GENERATE_REPORT");

        var stale = workers!.Single(w => w.Id == staleId);
        stale.IsStale.Should().BeTrue();
        stale.HeartbeatAgeSeconds.Should().BeGreaterThan(290);
    }

    [Fact]
    public async Task Health_reports_healthy_database()
    {
        var response = await _client.GetAsync("/api/v1/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Fact]
    public async Task Kubernetes_probes_are_anonymous_and_only_readiness_checks_the_database()
    {
        var anonymous = factory.CreateClient();

        var live = await anonymous.GetFromJsonAsync<ProbeResponse>("/api/v1/health/live");
        var ready = await anonymous.GetFromJsonAsync<ProbeResponse>("/api/v1/health/ready");

        live!.Status.Should().Be("Healthy");
        live.Checks.Should().BeEmpty("a database outage must not restart API pods");
        ready!.Status.Should().Be("Healthy");
        ready.Checks.Keys.Should().Equal("postgres");
    }

    [Fact]
    public async Task An_unreachable_database_answers_503_with_retry_after_not_500()
    {
        await using var api = factory.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Reeve"] = "Host=localhost;Port=1;Database=reeve;Username=x;Password=x;Timeout=2",
        });

        var response = await api.CreateClientAs(Roles.Viewer).GetAsync("/api/v1/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        (await response.Content.ReadFromJsonAsync<ProblemResponse>(ReeveJson.Options))!.Code.Should().Be("unavailable");
    }

    private sealed record ProbeResponse(string Status, Dictionary<string, ProbeCheck> Checks);

    private sealed record ProbeCheck(string Status, string? Description);

    [Fact]
    public async Task Correlation_id_is_echoed_or_generated()
    {
        var supplied = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        supplied.Headers.Add("X-Correlation-ID", "abc-123");
        (await _client.SendAsync(supplied)).Headers.GetValues("X-Correlation-ID").Should().Equal("abc-123");

        var generated = await _client.GetAsync("/api/v1/health");
        generated.Headers.GetValues("X-Correlation-ID").Single().Should().MatchRegex("^[0-9a-f]{32}$");

        var unsafeValue = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        unsafeValue.Headers.TryAddWithoutValidation("X-Correlation-ID", "<script>alert(1)</script>");
        (await _client.SendAsync(unsafeValue)).Headers.GetValues("X-Correlation-ID").Single()
            .Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details()
    {
        var response = await _client.GetAsync("/api/v1/nothing-here");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }
}

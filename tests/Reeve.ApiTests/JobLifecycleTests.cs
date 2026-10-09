using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Serialization;

namespace Reeve.ApiTests;

[Collection(ApiCollection.Name)]
public class JobLifecycleTests(ReeveApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClientAs(Roles.Admin);

    private Task<JobResponse> CreateAsync() =>
        factory.CreateJobAsync(_client, new CreateJobRequest("GENERATE_REPORT", JobPriority.Low));

    private async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(ReeveJson.Options))!;

    [Fact]
    public async Task Get_returns_job()
    {
        var created = await CreateAsync();

        var response = await _client.GetAsync($"/api/v1/jobs/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync<JobResponse>(response)).Should().BeEquivalentTo(created, o => o
            .Excluding(j => j.Payload)
            .Using<DateTimeOffset>(c => c.Subject.Should().BeCloseTo(c.Expectation, TimeSpan.FromMilliseconds(1)))
            .WhenTypeIs<DateTimeOffset>());
    }

    [Fact]
    public async Task Unknown_job_returns_404_problem()
    {
        var response = await _client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync<ProblemResponse>(response)).Code.Should().Be("not_found");
    }

    [Fact]
    public async Task Cancel_stops_a_pending_job_once()
    {
        var job = await CreateAsync();

        var first = await _client.PostAsync($"/api/v1/jobs/{job.Id}/cancel", null);
        var second = await _client.PostAsync($"/api/v1/jobs/{job.Id}/cancel", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await ReadAsync<JobResponse>(first);
        cancelled.Status.Should().Be(JobStatus.Cancelled);
        cancelled.CompletedAt.Should().NotBeNull();

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ProblemResponse>(second)).Code.Should().Be("invalid_state_transition");
    }

    [Fact]
    public async Task Retry_is_only_allowed_for_failed_or_dead_lettered_jobs()
    {
        var job = await CreateAsync();

        var tooEarly = await _client.PostAsync($"/api/v1/jobs/{job.Id}/retry", null);
        tooEarly.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await factory.UpdateJobAsync(job.Id, j =>
        {
            j.Start("worker-1", DateTimeOffset.UtcNow);
            j.Fail("Customer not found", isTransient: false, DateTimeOffset.UtcNow, Random.Shared);
        });

        var retried = await _client.PostAsync($"/api/v1/jobs/{job.Id}/retry", null);

        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadAsync<JobResponse>(retried);
        body.Status.Should().Be(JobStatus.Pending);
        body.LastError.Should().Be("Customer not found");
    }

    [Fact]
    public async Task Cancel_and_retry_of_unknown_job_return_404()
    {
        var id = Guid.NewGuid();

        (await _client.PostAsync($"/api/v1/jobs/{id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.PostAsync($"/api/v1/jobs/{id}/retry", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Attempts_show_execution_history_in_order()
    {
        var job = await CreateAsync();
        var start = DateTimeOffset.UtcNow;
        await factory.UpdateJobAsync(job.Id, j =>
        {
            j.Start("worker-1", start);
            j.Fail("Connection reset", isTransient: true, start.AddSeconds(2), Random.Shared);
            j.Start("worker-2", start.AddSeconds(10));
            j.Succeed(start.AddSeconds(13));
        });

        var response = await _client.GetAsync($"/api/v1/jobs/{job.Id}/attempts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var attempts = await ReadAsync<List<JobAttemptResponse>>(response);
        attempts.Select(a => (a.AttemptNumber, a.WorkerId, a.Status)).Should().Equal(
            (1, "worker-1", JobAttemptStatus.Failed),
            (2, "worker-2", JobAttemptStatus.Succeeded));
        attempts[0].Error.Should().Be("Connection reset");
        attempts[0].DurationMs.Should().BeApproximately(2000, 1);
        attempts[1].DurationMs.Should().BeApproximately(3000, 1);
    }

    [Fact]
    public async Task Attempts_of_unknown_job_return_404()
    {
        (await _client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}/attempts"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

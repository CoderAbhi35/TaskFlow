using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Serialization;
using Reeve.Contracts.Stats;

namespace Reeve.ApiTests;

[Collection(ApiCollection.Name)]
public class OverviewTests(ReeveApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClientAs(Roles.Admin);

    private async Task<OverviewResponse> GetAsync(int windowMinutes = 60) =>
        (await _client.GetFromJsonAsync<OverviewResponse>($"/api/v1/stats/overview?windowMinutes={windowMinutes}", ReeveJson.Options))!;

    [Fact]
    public async Task Reflects_submissions_outcomes_durations_and_backlog()
    {
        // Other tests share the database, so compare against a baseline instead of absolute numbers.
        var before = await GetAsync();
        var type = await factory.RegisterJobTypeAsync();
        var jobs = new List<JobResponse>();
        for (var i = 0; i < 5; i++)
            jobs.Add(await factory.CreateJobAsync(_client, new CreateJobRequest(type, RetryPolicy: new RetryPolicyDto(0, 1))));

        var now = DateTimeOffset.UtcNow;
        await factory.UpdateJobAsync(jobs[0].Id, j => { j.Start("w", now.AddMilliseconds(-100)); j.Succeed(now); });
        await factory.UpdateJobAsync(jobs[1].Id, j => { j.Start("w", now.AddMilliseconds(-300)); j.Succeed(now); });
        await factory.UpdateJobAsync(jobs[2].Id, j => { j.Start("w", now); j.Fail("bad input", false, now, Random.Shared); });
        await factory.UpdateJobAsync(jobs[3].Id, j => { j.Start("w", now); j.Fail("timeout", true, now, Random.Shared); });
        await factory.UpdateJobAsync(jobs[4].Id, j => j.Start("w", now));

        var after = await GetAsync();

        (after.Submitted - before.Submitted).Should().Be(5);
        (after.Succeeded - before.Succeeded).Should().Be(2);
        (after.Failed - before.Failed).Should().Be(1);
        (after.DeadLettered - before.DeadLettered).Should().Be(1, "no retries were allowed");
        (after.FailedAttempts - before.FailedAttempts).Should().Be(2);
        (after.Backlog.Running - before.Backlog.Running).Should().Be(1);
        after.SuccessRate.Should().BeInRange(0, 1);
        after.ThroughputPerMinute.Should().BeGreaterThan(0);
        after.P50DurationMs.Should().BeGreaterThan(0);
        after.P95DurationMs.Should().BeGreaterThanOrEqualTo(after.P50DurationMs!.Value);

        // The series covers the whole window and the jobs that just finished land in the last bucket.
        after.BucketMinutes.Should().Be(1);
        after.Series.Should().HaveCountGreaterThanOrEqualTo(60);
        after.Series.Should().BeInAscendingOrder(b => b.Start);
        after.Series.Sum(b => b.Succeeded).Should().Be(after.Succeeded);
        after.Series.Sum(b => b.Failed).Should().Be(after.Failed + after.DeadLettered);
    }

    [Fact]
    public async Task Longer_windows_use_wider_buckets()
    {
        var day = await GetAsync(24 * 60);

        day.BucketMinutes.Should().Be(24);
        day.Series.Count.Should().BeInRange(60, 62);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10_000)]
    public async Task Window_is_validated(int minutes)
    {
        var response = await _client.GetAsync($"/api/v1/stats/overview?windowMinutes={minutes}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ProblemResponse>(ReeveJson.Options))!.Errors!.Should().ContainKey("windowMinutes");
    }
}

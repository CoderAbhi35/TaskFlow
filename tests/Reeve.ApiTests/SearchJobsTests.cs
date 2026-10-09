using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;
using Reeve.Contracts.Common;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Serialization;

namespace Reeve.ApiTests;

[Collection(ApiCollection.Name)]
public class SearchJobsTests(ReeveApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClientAs(Roles.Admin);

    private async Task<PagedResponse<JobSummaryResponse>> SearchAsync(string query)
    {
        var response = await _client.GetAsync($"/api/v1/jobs?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResponse<JobSummaryResponse>>(ReeveJson.Options))!;
    }

    [Fact]
    public async Task Pages_through_results_newest_first_without_gaps_or_duplicates()
    {
        var type = await factory.RegisterJobTypeAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
            created.Add((await factory.CreateJobAsync(_client, new CreateJobRequest(type))).Id);

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await SearchAsync($"jobType={type}&limit=2" + (cursor is null ? "" : $"&cursor={cursor}"));
            seen.AddRange(page.Items.Select(j => j.Id));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null);

        pages.Should().Be(3);
        seen.Should().Equal(Enumerable.Reverse(created));
    }

    [Fact]
    public async Task Filters_by_status_and_priority()
    {
        var type = await factory.RegisterJobTypeAsync();
        var high = await factory.CreateJobAsync(_client, new CreateJobRequest(type, JobPriority.High));
        var low = await factory.CreateJobAsync(_client, new CreateJobRequest(type, JobPriority.Low));
        var deadLettered = await factory.CreateJobAsync(_client, new CreateJobRequest(type, JobPriority.Low));
        await factory.UpdateJobAsync(deadLettered.Id, j =>
        {
            j.Start("w", DateTimeOffset.UtcNow);
            j.Fail("boom", isTransient: false, DateTimeOffset.UtcNow, Random.Shared);
        });
        await _client.PostAsync($"/api/v1/jobs/{low.Id}/cancel", null);

        (await SearchAsync($"jobType={type}&priority=HIGH")).Items.Select(j => j.Id).Should().Equal(high.Id);
        (await SearchAsync($"jobType={type}&status=CANCELLED&status=FAILED")).Items.Select(j => j.Id)
            .Should().Equal(deadLettered.Id, low.Id);
        (await SearchAsync($"jobType={type}&status=pending")).Items.Select(j => j.Id).Should().Equal(high.Id);
    }

    [Fact]
    public async Task Filters_by_creation_time()
    {
        var type = await factory.RegisterJobTypeAsync();
        await factory.CreateJobAsync(_client, new CreateJobRequest(type));
        var cutoff = DateTimeOffset.UtcNow;
        await Task.Delay(20);
        var newer = await factory.CreateJobAsync(_client, new CreateJobRequest(type));

        var result = await SearchAsync($"jobType={type}&createdAfter={Uri.EscapeDataString(cutoff.ToString("O"))}");

        result.Items.Select(j => j.Id).Should().Equal(newer.Id);
    }

    [Theory]
    [InlineData("status=EXPLODED", "status")]
    [InlineData("status=3", "status")]
    [InlineData("priority=MEGA", "priority")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=1000", "limit")]
    [InlineData("cursor=not-a-cursor", "cursor")]
    [InlineData("createdAfter=2026-02-01T00:00:00Z&createdBefore=2026-01-01T00:00:00Z", "createdAfter")]
    public async Task Invalid_filters_return_validation_problem(string query, string field)
    {
        var response = await _client.GetAsync($"/api/v1/jobs?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemResponse>(ReeveJson.Options))!;
        problem.Code.Should().Be("validation_failed");
        problem.Errors!.Should().ContainKey(field);
    }

    [Fact]
    public async Task List_items_do_not_include_payloads()
    {
        var type = await factory.RegisterJobTypeAsync();
        await factory.CreateJobAsync(_client, new CreateJobRequest(type,
            Payload: System.Text.Json.JsonDocument.Parse("""{ "secret": "value" }""").RootElement));

        var raw = await _client.GetStringAsync($"/api/v1/jobs?jobType={type}");

        raw.Should().NotContain("payload").And.NotContain("secret");
    }
}

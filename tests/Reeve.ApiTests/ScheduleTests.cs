using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Schedules;
using Reeve.Contracts.Serialization;

namespace Reeve.ApiTests;

[Collection(ApiCollection.Name)]
public class ScheduleTests(ReeveApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClientAs(Roles.Admin);

    private static string UniqueName() => $"Schedule {Guid.NewGuid():N}";

    private Task<HttpResponseMessage> PostAsync(string json) =>
        _client.PostAsync("/api/v1/schedules", new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<ScheduleResponse> CreateAsync(string? name = null, string cron = "0 2 * * *", string? timeZone = null)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/schedules",
            new CreateScheduleRequest(name ?? UniqueName(), "GENERATE_REPORT", cron, timeZone, JobPriority.High),
            ReeveJson.Options);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ScheduleResponse>(ReeveJson.Options))!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(ReeveJson.Options))!;

    [Fact]
    public async Task Create_computes_the_first_run_in_the_schedule_time_zone()
    {
        var name = UniqueName();
        var response = await PostAsync($$"""
            {
              "name": "{{name}}",
              "jobType": "GENERATE_REPORT",
              "cronExpression": "30 9 * * *",
              "timeZone": "Asia/Kolkata",
              "priority": "HIGH",
              "payload": { "customerId": 7 },
              "retryPolicy": { "maxRetries": 2, "backoffSeconds": 60 }
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var schedule = await ReadAsync<ScheduleResponse>(response);
        response.Headers.Location!.ToString().Should().Be($"/api/v1/schedules/{schedule.Id}");
        schedule.Name.Should().Be(name);
        schedule.Enabled.Should().BeTrue();
        schedule.TimeZone.Should().Be("Asia/Kolkata");
        schedule.RetryPolicy.Should().Be(new RetryPolicyDto(2, 60));
        schedule.Payload.GetProperty("customerId").GetInt32().Should().Be(7);

        // 09:30 IST is 04:00 UTC.
        schedule.NextRunAt!.Value.UtcDateTime.TimeOfDay.Should().Be(new TimeSpan(4, 0, 0));
        schedule.NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow).And.BeBefore(DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public async Task Defaults_to_utc_and_the_job_type_retry_policy()
    {
        var schedule = await CreateAsync();

        schedule.TimeZone.Should().Be("UTC");
        schedule.RetryPolicy.MaxRetries.Should().Be(3); // seeded policy for GENERATE_REPORT
    }

    [Theory]
    [InlineData("""{ "name": "x", "jobType": "GENERATE_REPORT", "cronExpression": "every day" }""", "cronExpression")]
    [InlineData("""{ "name": "x", "jobType": "GENERATE_REPORT", "cronExpression": "0 0 2 * * *" }""", "cronExpression")]
    [InlineData("""{ "name": "x", "jobType": "GENERATE_REPORT", "cronExpression": "0 2 * * *", "timeZone": "Moon/Base" }""", "timeZone")]
    [InlineData("""{ "name": "x", "jobType": "NO_SUCH_TYPE", "cronExpression": "0 2 * * *" }""", "jobType")]
    [InlineData("""{ "name": "", "jobType": "GENERATE_REPORT", "cronExpression": "0 2 * * *" }""", "name")]
    [InlineData("""{ "name": "x", "jobType": "GENERATE_REPORT", "cronExpression": "0 0 30 2 *" }""", "cronExpression")]
    public async Task Invalid_schedules_are_rejected(string body, string field)
    {
        var response = await PostAsync(body.Replace("\"name\": \"x\"", $"\"name\": \"{UniqueName()}\""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync<ProblemResponse>(response)).Errors!.Should().ContainKey(field);
    }

    [Fact]
    public async Task Names_are_unique()
    {
        var name = UniqueName();
        await CreateAsync(name);

        var response = await _client.PostAsJsonAsync("/api/v1/schedules",
            new CreateScheduleRequest(name, "SEND_NOTIFICATION", "* * * * *"), ReeveJson.Options);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ProblemResponse>(response)).Code.Should().Be("schedule_name_taken");
    }

    [Fact]
    public async Task Pause_and_resume_are_idempotent()
    {
        var schedule = await CreateAsync(cron: "*/10 * * * *");

        var paused = await ReadAsync<ScheduleResponse>(await _client.PostAsync($"/api/v1/schedules/{schedule.Id}/pause", null));
        var pausedAgain = await _client.PostAsync($"/api/v1/schedules/{schedule.Id}/pause", null);
        paused.Enabled.Should().BeFalse();
        pausedAgain.StatusCode.Should().Be(HttpStatusCode.OK);

        var resumed = await ReadAsync<ScheduleResponse>(await _client.PostAsync($"/api/v1/schedules/{schedule.Id}/resume", null));
        var resumedAgain = await _client.PostAsync($"/api/v1/schedules/{schedule.Id}/resume", null);
        resumed.Enabled.Should().BeTrue();
        resumed.NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow);
        resumedAgain.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task List_get_and_delete()
    {
        var schedule = await CreateAsync();

        var all = await _client.GetFromJsonAsync<List<ScheduleResponse>>("/api/v1/schedules", ReeveJson.Options);
        all!.Should().Contain(s => s.Id == schedule.Id);

        (await _client.GetAsync($"/api/v1/schedules/{schedule.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.DeleteAsync($"/api/v1/schedules/{schedule.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/v1/schedules/{schedule.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.DeleteAsync($"/api/v1/schedules/{schedule.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;
using Reeve.Contracts.Audit;
using Reeve.Contracts.Common;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Schedules;
using Reeve.Contracts.Serialization;

namespace Reeve.ApiTests;

[Collection(ApiCollection.Name)]
public class AuditTests(ReeveApiFactory factory)
{
    private readonly HttpClient _admin = factory.CreateClientAs(Roles.Admin);

    private async Task<List<AuditEventResponse>> AuditForAsync(string entityId) =>
        (await _admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>($"/api/v1/audit?entityId={entityId}", ReeveJson.Options))!
        .Items.ToList();

    [Fact]
    public async Task Job_actions_are_recorded_with_actor_correlation_id_and_details()
    {
        var operatorClient = factory.CreateClientAs(Roles.Operator, "olivia");
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs")
        {
            Content = JsonContent.Create(new CreateJobRequest("GENERATE_REPORT", JobPriority.High,
                RetryPolicy: new RetryPolicyDto(0, 1)), options: ReeveJson.Options),
        };
        create.Headers.Add("X-Correlation-ID", "audit-test-create");
        var job = (await (await operatorClient.SendAsync(create)).Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options))!;

        await factory.UpdateJobAsync(job.Id, j =>
        {
            j.Start("w", DateTimeOffset.UtcNow);
            j.Fail("boom", false, DateTimeOffset.UtcNow, Random.Shared);
        });
        await operatorClient.PostAsync($"/api/v1/jobs/{job.Id}/retry", null);
        await factory.CreateClientAs(Roles.Admin, "ada").PostAsync($"/api/v1/jobs/{job.Id}/cancel", null);

        var events = await AuditForAsync(job.Id.ToString());

        events.Select(e => (e.Action, e.Actor)).Should().Equal(
            ("job.cancelled", "ada"),
            ("job.retried", "olivia"),
            ("job.created", "olivia"));
        var created = events[^1];
        created.EntityType.Should().Be("job");
        created.CorrelationId.Should().Be("audit-test-create");
        created.Details!.Value.GetProperty("type").GetString().Should().Be("GENERATE_REPORT");
        created.Details.Value.GetProperty("priority").GetString().Should().Be("High");
        created.Details.Value.TryGetProperty("payload", out _).Should().BeFalse("payloads are never copied into the audit log");
    }

    [Fact]
    public async Task Idempotent_replays_are_not_audited_twice()
    {
        var key = $"audit-{Guid.NewGuid()}";
        JobResponse? job = null;
        for (var i = 0; i < 3; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs")
            {
                Content = JsonContent.Create(new CreateJobRequest("SEND_NOTIFICATION"), options: ReeveJson.Options),
            };
            request.Headers.Add("Idempotency-Key", key);
            job = await (await _admin.SendAsync(request)).Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options);
        }

        (await AuditForAsync(job!.Id.ToString())).Should().ContainSingle(e => e.Action == "job.created");
    }

    [Fact]
    public async Task A_rejected_change_leaves_no_audit_record()
    {
        var job = await factory.CreateJobAsync(_admin, new CreateJobRequest("SEND_NOTIFICATION"));
        await factory.UpdateJobAsync(job.Id, j => { j.Start("w", DateTimeOffset.UtcNow); j.Succeed(DateTimeOffset.UtcNow); });

        var cancel = await _admin.PostAsync($"/api/v1/jobs/{job.Id}/cancel", null);

        cancel.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AuditForAsync(job.Id.ToString())).Select(e => e.Action).Should().Equal("job.created");
    }

    [Fact]
    public async Task Schedule_changes_are_recorded_once_per_real_change()
    {
        var created = await _admin.PostAsJsonAsync("/api/v1/schedules",
            new CreateScheduleRequest($"Audited {Guid.NewGuid():N}", "GENERATE_REPORT", "0 2 * * *"), ReeveJson.Options);
        var schedule = (await created.Content.ReadFromJsonAsync<ScheduleResponse>(ReeveJson.Options))!;

        await _admin.PostAsync($"/api/v1/schedules/{schedule.Id}/pause", null);
        await _admin.PostAsync($"/api/v1/schedules/{schedule.Id}/pause", null); // no-op: already paused
        await _admin.PostAsync($"/api/v1/schedules/{schedule.Id}/resume", null);
        await _admin.DeleteAsync($"/api/v1/schedules/{schedule.Id}");

        (await AuditForAsync(schedule.Id.ToString())).Select(e => e.Action).Should().Equal(
            "schedule.deleted", "schedule.resumed", "schedule.paused", "schedule.created");
    }

    [Fact]
    public async Task Sign_in_attempts_are_recorded()
    {
        var username = $"user-{Guid.NewGuid():N}"[..20];
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new TokenRequest(username, "guess"));
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new TokenRequest("viewer", "viewer-password"));

        // A failed attempt is not attributed to the (untrusted) username that was typed.
        (await AuditForAsync(username)).Should().ContainSingle(e => e.Action == "auth.login_failed" && e.Actor == "anonymous");
        // A successful one is attributed to the user it authenticated.
        (await AuditForAsync("viewer")).Should().Contain(e => e.Action == "auth.token_issued" && e.Actor == "viewer");
    }

    [Fact]
    public async Task Audit_log_can_be_filtered_and_paged()
    {
        var actor = $"pager-{Guid.NewGuid():N}"[..20];
        var client = factory.CreateClientAs(Roles.Operator, actor);
        for (var i = 0; i < 3; i++)
            await factory.CreateJobAsync(client, new CreateJobRequest("SEND_NOTIFICATION"));

        var first = (await _admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>($"/api/v1/audit?actor={actor}&limit=2", ReeveJson.Options))!;
        var second = (await _admin.GetFromJsonAsync<PagedResponse<AuditEventResponse>>($"/api/v1/audit?actor={actor}&limit=2&cursor={first.NextCursor}", ReeveJson.Options))!;

        first.Items.Should().HaveCount(2);
        second.Items.Should().ContainSingle();
        second.NextCursor.Should().BeNull();
        first.Items.Concat(second.Items).Should().OnlyContain(e => e.Actor == actor && e.Action == "job.created");
    }
}

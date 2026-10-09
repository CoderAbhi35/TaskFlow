using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Serialization;

namespace Reeve.ApiTests;

[Collection(ApiCollection.Name)]
public class CreateJobTests(ReeveApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClientAs(Roles.Admin);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private Task<HttpResponseMessage> PostRawAsync(string json, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _client.SendAsync(request);
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        return (await response.Content.ReadFromJsonAsync<ProblemResponse>(ReeveJson.Options))!;
    }

    [Fact]
    public async Task Creates_job_and_returns_location()
    {
        var response = await PostRawAsync("""
            {
              "jobType": "GENERATE_REPORT",
              "priority": "HIGH",
              "payload": { "customerId": 12345, "reportType": "MONTHLY" },
              "retryPolicy": { "maxRetries": 3, "backoffSeconds": 30 }
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var job = (await response.Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options))!;
        response.Headers.Location!.ToString().Should().Be($"/api/v1/jobs/{job.Id}");

        job.JobType.Should().Be("GENERATE_REPORT");
        job.Status.Should().Be(JobStatus.Pending);
        job.Priority.Should().Be(JobPriority.High);
        job.RetryPolicy.Should().Be(new RetryPolicyDto(3, 30));
        job.Payload.GetProperty("customerId").GetInt32().Should().Be(12345);
        job.AttemptCount.Should().Be(0);

        // Enums are part of the public contract: SNAKE_UPPER strings, never numbers.
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().Contain("\"priority\":\"HIGH\"").And.Contain("\"status\":\"PENDING\"");
    }

    [Fact]
    public async Task Defaults_come_from_the_job_type()
    {
        var job = await factory.CreateJobAsync(_client, new CreateJobRequest("SEND_NOTIFICATION"));

        job.Priority.Should().Be(JobPriority.Normal);
        job.RetryPolicy.MaxRetries.Should().Be(5); // seeded policy for SEND_NOTIFICATION
        job.Payload.GetRawText().Should().Be("{}");
    }

    [Fact]
    public async Task Invalid_fields_are_reported_together()
    {
        var response = await PostRawAsync("""
            { "jobType": "", "payload": [1, 2], "retryPolicy": { "maxRetries": 99, "backoffSeconds": 0 } }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.Code.Should().Be("validation_failed");
        problem.CorrelationId.Should().NotBeNullOrEmpty();
        problem.Errors!.Keys.Should().BeEquivalentTo(
            "jobType", "payload", "retryPolicy.maxRetries", "retryPolicy.backoffSeconds");
    }

    [Fact]
    public async Task Unregistered_job_type_is_rejected()
    {
        var response = await PostRawAsync("""{ "jobType": "RUN_ARBITRARY_CODE" }""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Errors!.Should().ContainKey("jobType");
    }

    [Fact]
    public async Task Disabled_job_type_is_rejected()
    {
        var type = await factory.RegisterJobTypeAsync(enabled: false);

        var response = await PostRawAsync($$"""{ "jobType": "{{type}}" }""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Errors!["jobType"].Single().Should().Contain("disabled");
    }

    [Theory]
    [InlineData("""{ "jobType": "GENERATE_REPORT", "priority": "URGENT" }""")]
    [InlineData("""{ "jobType": "GENERATE_REPORT", "priority": 2 }""")]
    [InlineData("""{ "jobType": "GENERATE_REPORT", """)]
    [InlineData("not json")]
    public async Task Unreadable_body_returns_400_problem(string body)
    {
        var response = await PostRawAsync(body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Code.Should().Be("invalid_request");
    }

    [Fact]
    public async Task Oversized_payload_is_rejected()
    {
        var bigPayload = JsonSerializer.Serialize(new { data = new string('x', 70 * 1024) });

        var response = await PostRawAsync($$"""{ "jobType": "GENERATE_REPORT", "payload": {{bigPayload}} }""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Errors!.Should().ContainKey("payload");
    }

    [Fact]
    public async Task Scheduling_too_far_ahead_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/jobs",
            new CreateJobRequest("GENERATE_REPORT", ScheduledAt: DateTimeOffset.UtcNow.AddYears(2)), ReeveJson.Options);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Errors!.Should().ContainKey("scheduledAt");
    }

    [Fact]
    public async Task Repeating_a_request_with_the_same_idempotency_key_returns_the_original_job()
    {
        var key = $"order-{Guid.NewGuid()}";

        var first = await PostRawAsync("""{ "jobType": "GENERATE_REPORT", "payload": { "a": 1 } }""", key);
        // Same content, different formatting: still the same request.
        var second = await PostRawAsync("""{"jobType":"GENERATE_REPORT","payload":{"a":1}}""", key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");

        var firstJob = await first.Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options);
        var secondJob = await second.Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options);
        secondJob!.Id.Should().Be(firstJob!.Id);
        secondJob.IdempotencyKey.Should().Be(key);
    }

    [Fact]
    public async Task Idempotency_keys_belong_to_the_caller()
    {
        // Two clients happen to pick the same key. Each gets its own job, and neither can see the
        // other's job (or learn that the key is taken) through the key.
        var key = $"order-{Guid.NewGuid()}";
        const string body = """{ "jobType": "GENERATE_REPORT", "payload": { "a": 1 } }""";
        async Task<HttpResponseMessage> PostAs(string user, string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", key);
            return await factory.CreateClientAs(Roles.Operator, user).SendAsync(request);
        }

        var alice = await PostAs("alice", body);
        var bob = await PostAs("bob", body);
        var bobDifferent = await PostAs("carol", """{ "jobType": "GENERATE_REPORT", "payload": { "b": 2 } }""");
        var aliceAgain = await PostAs("alice", body);

        alice.StatusCode.Should().Be(HttpStatusCode.Created);
        bob.StatusCode.Should().Be(HttpStatusCode.Created, "bob's key is his own, not a replay of alice's job");
        bobDifferent.StatusCode.Should().Be(HttpStatusCode.Created, "not a 422: the key isn't taken for carol");
        aliceAgain.StatusCode.Should().Be(HttpStatusCode.OK, "alice's own replay still returns her job");

        var aliceJob = await alice.Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options);
        var bobJob = await bob.Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options);
        var replayed = await aliceAgain.Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options);
        bobJob!.Id.Should().NotBe(aliceJob!.Id);
        replayed!.Id.Should().Be(aliceJob.Id);
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_request_is_rejected()
    {
        var key = $"order-{Guid.NewGuid()}";
        (await PostRawAsync("""{ "jobType": "GENERATE_REPORT", "payload": { "a": 1 } }""", key))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await PostRawAsync("""{ "jobType": "GENERATE_REPORT", "payload": { "a": 2 } }""", key);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadProblemAsync(response)).Code.Should().Be("idempotency_key_reused");
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_idempotency_key_create_exactly_one_job()
    {
        var key = $"order-{Guid.NewGuid()}";
        const string body = """{ "jobType": "GENERATE_REPORT", "payload": { "a": 1 } }""";

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PostRawAsync(body, key)));

        responses.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.Created || s == HttpStatusCode.OK);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        var ids = await Task.WhenAll(responses.Select(async r =>
            (await r.Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options))!.Id));
        ids.Distinct().Should().ContainSingle();
    }

    // An empty header value can't be sent through the in-memory test client, which drops it.
    // Verified manually against Kestrel: it returns 400 like the cases below.
    [Theory]
    [InlineData("has space")]
    [InlineData("é-non-ascii")]
    [InlineData(null)] // 201 characters
    public async Task Invalid_idempotency_key_is_rejected(string? key)
    {
        var response = await PostRawAsync("""{ "jobType": "GENERATE_REPORT" }""", key ?? new string('k', 201));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Errors!.Should().ContainKey("Idempotency-Key");
    }
}

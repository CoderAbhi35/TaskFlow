using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;
using Reeve.Application.Telemetry;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Serialization;

namespace Reeve.ApiTests;

[Collection(ApiCollection.Name)]
public class TelemetryTests(ReeveApiFactory factory)
{
    [Fact]
    public async Task A_job_remembers_the_trace_of_the_request_that_created_it()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs")
        {
            Content = JsonContent.Create(new CreateJobRequest("SEND_NOTIFICATION"), options: ReeveJson.Options),
        };
        // The caller is already tracing (e.g. an upstream service): the API joins that trace.
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        var response = await factory.CreateClientAs(Roles.Operator).SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var job = (await response.Content.ReadFromJsonAsync<JobResponse>(ReeveJson.Options))!;

        string? stored = null;
        await factory.WithDbAsync(async db => stored = (await db.Jobs.SingleAsync(j => j.Id == job.Id)).TraceParent);
        stored.Should().StartWith($"00-{traceId}-", "dispatch and execution will continue this trace");
    }

    [Fact]
    public async Task Submissions_are_counted_by_job_type_and_source()
    {
        var type = await factory.RegisterJobTypeAsync();
        long submitted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReeveTelemetry.Name && instrument.Name == "reeve.jobs.submitted")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var t = tags.ToArray().ToDictionary(x => x.Key, x => x.Value);
            if (Equals(t["job_type"], type) && Equals(t["source"], "api"))
                Interlocked.Add(ref submitted, value);
        });
        listener.Start();

        var client = factory.CreateClientAs(Roles.Operator);
        await factory.CreateJobAsync(client, new CreateJobRequest(type));
        await factory.CreateJobAsync(client, new CreateJobRequest(type));

        // An idempotent replay is not a new submission.
        for (var i = 0; i < 2; i++)
        {
            var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs")
            {
                Content = JsonContent.Create(new CreateJobRequest(type), options: ReeveJson.Options),
            };
            replay.Headers.Add("Idempotency-Key", $"telemetry-{type}");
            await client.SendAsync(replay);
        }

        Interlocked.Read(ref submitted).Should().Be(3);
    }
}

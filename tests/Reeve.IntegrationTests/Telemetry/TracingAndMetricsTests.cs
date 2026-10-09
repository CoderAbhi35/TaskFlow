using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Application.Abstractions;
using Reeve.Application.Execution;
using Reeve.Domain.Jobs;
using Reeve.IntegrationTests.Persistence;
using Reeve.IntegrationTests.Worker;

namespace Reeve.IntegrationTests.Telemetry;

[Collection(PostgresCollection.Name)]
public class TracingAndMetricsTests(PostgresFixture fixture)
{
    private static string NewTraceParent(out ActivityTraceId traceId)
    {
        traceId = ActivityTraceId.CreateRandom();
        return $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01";
    }

    private async Task<Job> AddJobAsync(string type, string? traceParent, RetryPolicy? retryPolicy = null)
    {
        var job = Job.Create(type, null, JobPriority.Normal, retryPolicy ?? RetryPolicy.Default, DateTimeOffset.UtcNow,
            traceParent: traceParent);
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Reeve.Infrastructure.Persistence.ReeveDbContext>();
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private async Task<ServiceProvider> ExecutorWith(IJobHandler handler) =>
        await Task.FromResult(fixture.BuildServices(s =>
        {
            s.AddSingleton(handler);
            s.AddSingleton<JobHandlerRegistry>();
            s.AddSingleton<JobExecutor>();
        }));

    private static async Task ExecuteAsync(ServiceProvider services, Guid jobId)
    {
        await using var scope = services.CreateAsyncScope();
        var claimed = await scope.ServiceProvider.GetRequiredService<IJobClaimer>().ClaimByIdAsync(jobId, "worker-t");
        await services.GetRequiredService<JobExecutor>().ExecuteAsync(claimed!, CancellationToken.None);
    }

    [Fact]
    public async Task Execution_continues_the_trace_stored_with_the_job()
    {
        using var capture = new TelemetryCapture();
        var type = await fixture.RegisterJobTypeAsync();
        var job = await AddJobAsync(type, NewTraceParent(out var traceId));
        await using var services = await ExecutorWith(DelegateJobHandler.Succeeds(type));

        await ExecuteAsync(services, job.Id);

        var span = capture.Spans.Single(s => Equals(s.GetTagItem("reeve.job.id"), job.Id));
        span.TraceId.Should().Be(traceId, "the attempt belongs to the trace of the request that created the job");
        span.Kind.Should().Be(ActivityKind.Consumer);
        span.DisplayName.Should().Be($"execute {type}");
        span.GetTagItem("reeve.job.attempt").Should().Be(1);
        span.GetTagItem("reeve.job.outcome").Should().Be("succeeded");
        span.Status.Should().Be(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task Execution_outcomes_are_counted_and_timed()
    {
        using var capture = new TelemetryCapture();
        var type = await fixture.RegisterJobTypeAsync();
        var handler = new DelegateJobHandler(type, async (ctx, _) =>
        {
            await Task.Delay(20);
            if (ctx.Payload.ToString().Contains("fail-permanent"))
                throw new PermanentJobFailureException("bad input");
            if (ctx.Payload.ToString().Contains("fail-transient"))
                throw new IOException("network");
        });
        await using var services = await ExecutorWith(handler);

        async Task<Guid> Run(string payload, int maxRetries)
        {
            var job = Job.Create(type, payload, JobPriority.Normal, new RetryPolicy(maxRetries, 1), DateTimeOffset.UtcNow);
            await using (var scope = fixture.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Reeve.Infrastructure.Persistence.ReeveDbContext>();
                db.Jobs.Add(job);
                await db.SaveChangesAsync();
            }
            await ExecuteAsync(services, job.Id);
            return job.Id;
        }

        await Run("""{"x":"ok"}""", 3);
        await Run("""{"x":"ok"}""", 3);
        await Run("""{"x":"fail-permanent"}""", 3);
        await Run("""{"x":"fail-transient"}""", 3);   // retry scheduled
        await Run("""{"x":"fail-transient"}""", 0);   // no retries left: dead-lettered

        (string, object) typeTag = ("job_type", type);
        capture.Sum("reeve.jobs.succeeded", typeTag).Should().Be(2);
        capture.Sum("reeve.jobs.retried", typeTag).Should().Be(1);
        capture.Sum("reeve.jobs.failed", typeTag, ("outcome", "failed")).Should().Be(1);
        capture.Sum("reeve.jobs.failed", typeTag, ("outcome", "dead_lettered")).Should().Be(1);

        var durations = capture.Measurements.Where(m => m.Instrument == "reeve.job.execution.duration" && Equals(m.Tags["job_type"], type)).ToList();
        durations.Should().HaveCount(5);
        durations.Should().OnlyContain(m => m.Value >= 0.015 && m.Value < 5, "seconds, not milliseconds");
        durations.Select(m => m.Tags["outcome"]).Should().BeEquivalentTo(["succeeded", "succeeded", "failed", "retried", "dead_lettered"]);

        capture.Spans.Where(s => Equals(s.GetTagItem("reeve.job.type"), type) && s.Status == ActivityStatusCode.Error)
            .Should().HaveCount(3, "failed attempts are marked as errors in the trace");
    }

    [Fact]
    public async Task A_job_without_a_stored_trace_starts_a_new_one()
    {
        using var capture = new TelemetryCapture();
        var type = await fixture.RegisterJobTypeAsync();
        var job = await AddJobAsync(type, traceParent: "not-a-traceparent");
        await using var services = await ExecutorWith(DelegateJobHandler.Succeeds(type));

        await ExecuteAsync(services, job.Id);

        var span = capture.Spans.Single(s => Equals(s.GetTagItem("reeve.job.id"), job.Id));
        span.ParentSpanId.Should().Be(default(ActivitySpanId));
    }
}

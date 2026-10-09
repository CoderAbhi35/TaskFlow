using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reeve.Application;
using Reeve.Application.Execution;
using Reeve.Domain.Jobs;
using Reeve.Domain.Workers;
using Reeve.Infrastructure;
using Reeve.Infrastructure.Persistence;
using Reeve.IntegrationTests.Persistence;
using Reeve.Worker;

namespace Reeve.IntegrationTests.Worker;

/// <summary>Runs the real worker host (all hosted services) against the test database, using the database transport.</summary>
[Collection(PostgresCollection.Name)]
public class WorkerEndToEndTests(PostgresFixture fixture)
{
    private IHost BuildWorkerHost(IJobHandler handler, int shutdownGraceSeconds = 5)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(fixture.BuildConfiguration(new Dictionary<string, string?>
        {
            ["Worker:Transport"] = "Database",
            ["Worker:Concurrency"] = "2",
            ["Worker:PollIntervalMs"] = "100",
            ["Worker:HeartbeatIntervalSeconds"] = "1",
            ["Worker:RecoveryIntervalSeconds"] = "60",
            ["Worker:ShutdownGraceSeconds"] = shutdownGraceSeconds.ToString(),
        }));
        builder.Services.AddReeveApplication();
        builder.Services.AddReevePersistence(builder.Configuration);
        builder.Services.AddReeveWorker(builder.Configuration);
        builder.Services.AddSingleton(handler);
        return builder.Build();
    }

    private async Task<Job> WaitForAsync(Guid jobId, Func<Job, bool> condition, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var job = await fixture.GetJobAsync(jobId);
            if (condition(job))
                return job;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Job {jobId} is {job.Status}; the expected state was not reached.");
            await Task.Delay(100);
        }
    }

    private async Task<WorkerNode> GetWorkerAsync(IHost host)
    {
        var id = host.Services.GetRequiredService<WorkerIdentity>().Id;
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ReeveDbContext>().Workers.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    [Fact]
    public async Task Worker_registers_executes_a_job_and_goes_offline_on_shutdown()
    {
        var type = await fixture.RegisterJobTypeAsync();
        using var host = BuildWorkerHost(DelegateJobHandler.Succeeds(type));

        await host.StartAsync();
        var worker = await GetWorkerAsync(host);
        worker.Status.Should().Be(WorkerStatus.Active);
        worker.SupportedJobTypes.Should().Equal(type);

        var job = await fixture.AddJobAsync(type);
        var done = await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);
        done.Attempts.Single().WorkerId.Should().Be(worker.Id);

        await host.StopAsync();
        (await GetWorkerAsync(host)).Status.Should().Be(WorkerStatus.Offline);
    }

    [Fact]
    public async Task Transient_failure_is_retried_and_then_succeeds()
    {
        var type = await fixture.RegisterJobTypeAsync();
        using var host = BuildWorkerHost(new DelegateJobHandler(type, (ctx, _) =>
            ctx.AttemptNumber == 1 ? throw new IOException("Connection reset") : Task.CompletedTask));
        await host.StartAsync();

        var job = await fixture.AddJobAsync(type, retryPolicy: new RetryPolicy(3, 1));
        var done = await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);

        done.Attempts.OrderBy(a => a.AttemptNumber).Select(a => a.Status)
            .Should().Equal(JobAttemptStatus.Failed, JobAttemptStatus.Succeeded);
        await host.StopAsync();
    }

    [Fact]
    public async Task Shutdown_waits_for_the_grace_period_then_hands_unfinished_jobs_back()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var started = new TaskCompletionSource();
        using var host = BuildWorkerHost(new DelegateJobHandler(type, async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }), shutdownGraceSeconds: 1);
        await host.StartAsync();

        var job = await fixture.AddJobAsync(type, retryPolicy: new RetryPolicy(3, 1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.StopAsync();

        var reloaded = await fixture.GetJobAsync(job.Id);
        reloaded.Status.Should().Be(JobStatus.Pending, "the job must go back to the queue, not stay Running");
        reloaded.LastError.Should().Be("The worker shut down before the job finished.");
        (await GetWorkerAsync(host)).Status.Should().Be(WorkerStatus.Offline);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Application.Abstractions;
using Reeve.Application.Execution;
using Reeve.Domain.Jobs;
using Reeve.Domain.Workers;
using Reeve.Infrastructure.Persistence;
using Reeve.IntegrationTests.Persistence;

namespace Reeve.IntegrationTests.Worker;

[Collection(PostgresCollection.Name)]
public class JobRecoveryTests(PostgresFixture fixture)
{
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

    private async Task<string> RegisterWorkerAsync(string type, TimeSpan heartbeatAge)
    {
        var id = $"w-{Guid.NewGuid():N}";
        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkerRegistry>()
            .RegisterAsync(WorkerNode.Register(id, "host", 2, [type], DateTimeOffset.UtcNow - heartbeatAge));
        return id;
    }

    private async Task<ClaimedJob> ClaimAsync(string workerId, string type)
    {
        await using var scope = fixture.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IJobClaimer>().ClaimAsync(workerId, [type], 1)).Single();
    }

    private async Task<RecoveryResult> RecoverAsync()
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IJobRecovery>().RecoverAsync(HeartbeatTimeout);
    }

    private async Task<WorkerNode> GetWorkerAsync(string id)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ReeveDbContext>().Workers.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    [Fact]
    public async Task Jobs_held_by_a_crashed_worker_are_returned_to_the_queue()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var crashed = await RegisterWorkerAsync(type, heartbeatAge: TimeSpan.FromMinutes(5));
        var healthy = await RegisterWorkerAsync(type, heartbeatAge: TimeSpan.Zero);
        var lostJob = await fixture.AddJobAsync(type, retryPolicy: new RetryPolicy(3, 1));
        await ClaimAsync(crashed, type);
        var safeJob = await fixture.AddJobAsync(type);
        await ClaimAsync(healthy, type);

        var result = await RecoverAsync();

        result.JobsRecovered.Should().BeGreaterThanOrEqualTo(1);
        var lost = await fixture.GetJobAsync(lostJob.Id);
        lost.Status.Should().Be(JobStatus.Pending);
        lost.RetryCount.Should().Be(1);
        lost.Attempts.Single().Status.Should().Be(JobAttemptStatus.Failed);
        lost.LastError.Should().Contain(crashed).And.Contain("stopped responding");

        (await fixture.GetJobAsync(safeJob.Id)).Status.Should().Be(JobStatus.Running);
        (await GetWorkerAsync(crashed)).Status.Should().Be(WorkerStatus.Offline);
        (await GetWorkerAsync(healthy)).Status.Should().Be(WorkerStatus.Active);
    }

    [Fact]
    public async Task Recovered_job_without_retries_left_is_dead_lettered()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var crashed = await RegisterWorkerAsync(type, heartbeatAge: TimeSpan.FromMinutes(5));
        var job = await fixture.AddJobAsync(type, retryPolicy: new RetryPolicy(0, 1));
        await ClaimAsync(crashed, type);

        await RecoverAsync();

        (await fixture.GetJobAsync(job.Id)).Status.Should().Be(JobStatus.DeadLettered);
    }

    [Fact]
    public async Task Late_result_from_a_worker_presumed_dead_does_not_overwrite_the_retry()
    {
        // The worker was only slow (e.g. a long GC pause or network partition), not dead.
        var type = await fixture.RegisterJobTypeAsync();
        var slow = await RegisterWorkerAsync(type, heartbeatAge: TimeSpan.FromMinutes(5));
        var healthy = await RegisterWorkerAsync(type, heartbeatAge: TimeSpan.Zero);
        var job = await fixture.AddJobAsync(type, retryPolicy: new RetryPolicy(3, 1));
        var slowClaim = await ClaimAsync(slow, type);

        await RecoverAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.5)); // let the retry backoff elapse
        var retryClaim = await ClaimAsync(healthy, type);

        await using var services = fixture.BuildServices(s =>
        {
            s.AddSingleton<IJobHandler>(DelegateJobHandler.Succeeds(type));
            s.AddSingleton<JobHandlerRegistry>();
            s.AddSingleton<JobExecutor>();
        });
        await services.GetRequiredService<JobExecutor>().ExecuteAsync(slowClaim, CancellationToken.None);

        var reloaded = await fixture.GetJobAsync(job.Id);
        retryClaim.AttemptNumber.Should().Be(2);
        reloaded.Status.Should().Be(JobStatus.Running, "attempt 2 is still in progress");
        reloaded.Attempts.OrderBy(a => a.AttemptNumber).Select(a => (a.WorkerId, a.Status)).Should().Equal(
            (slow, JobAttemptStatus.Failed),
            (healthy, JobAttemptStatus.Running));
    }

    [Fact]
    public async Task An_attempt_long_past_its_timeout_is_recovered_even_though_its_worker_is_alive()
    {
        // The worker heartbeats, but this attempt's result was never recorded (a database error while
        // recording, or a handler that never returned). Without recovery it would stay Running forever.
        var type = await fixture.RegisterJobTypeAsync(timeoutSeconds: 10);
        var alive = await RegisterWorkerAsync(type, heartbeatAge: TimeSpan.Zero);
        var stuck = await fixture.AddJobAsync(type, retryPolicy: new RetryPolicy(3, 1));
        await ClaimAsync(alive, type);
        var fresh = await fixture.AddJobAsync(type);
        await ClaimAsync(alive, type);

        // Started 2 minutes ago: past the 10 s timeout plus twice the 30 s heartbeat timeout.
        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReeveDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE job_attempts SET started_at = now() - interval '2 minutes' WHERE job_id = {stuck.Id}");
        }

        await RecoverAsync();

        var recovered = await fixture.GetJobAsync(stuck.Id);
        recovered.Status.Should().Be(JobStatus.Pending);
        recovered.RetryCount.Should().Be(1);
        recovered.LastError.Should().Contain("ran past its 10 s timeout");
        (await fixture.GetJobAsync(fresh.Id)).Status.Should().Be(JobStatus.Running, "its attempt is within its timeout");
        (await GetWorkerAsync(alive)).Status.Should().Be(WorkerStatus.Active, "the worker itself is healthy");
    }
}

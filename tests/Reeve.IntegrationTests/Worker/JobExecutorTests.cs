using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Application.Abstractions;
using Reeve.Application.Execution;
using Reeve.Domain.Jobs;
using Reeve.IntegrationTests.Persistence;

namespace Reeve.IntegrationTests.Worker;

[Collection(PostgresCollection.Name)]
public class JobExecutorTests(PostgresFixture fixture)
{
    /// <summary>Creates a job, claims it as "worker-1", and returns an executor wired to the handler.</summary>
    private async Task<(ClaimedJob Claimed, JobExecutor Executor, ServiceProvider Services)> ArrangeAsync(
        Func<JobExecutionContext, CancellationToken, Task> handle, int timeoutSeconds = 60, RetryPolicy? retryPolicy = null)
    {
        var type = await fixture.RegisterJobTypeAsync(timeoutSeconds);
        await fixture.AddJobAsync(type, retryPolicy: retryPolicy, payload: """{"orderId": 7}""");

        var services = fixture.BuildServices(s =>
        {
            s.AddSingleton<IJobHandler>(new DelegateJobHandler(type, handle));
            s.AddSingleton<JobHandlerRegistry>();
            s.AddSingleton<JobExecutor>();
        });

        await using var scope = services.CreateAsyncScope();
        var claimed = (await scope.ServiceProvider.GetRequiredService<IJobClaimer>().ClaimAsync("worker-1", [type], 1)).Single();
        return (claimed, services.GetRequiredService<JobExecutor>(), services);
    }

    [Fact]
    public async Task Successful_handler_completes_the_job()
    {
        JobExecutionContext? seen = null;
        var (claimed, executor, services) = await ArrangeAsync((ctx, _) => { seen = ctx; return Task.CompletedTask; });
        await using var _ = services;

        await executor.ExecuteAsync(claimed, CancellationToken.None);

        var job = await fixture.GetJobAsync(claimed.JobId);
        job.Status.Should().Be(JobStatus.Succeeded);
        job.Attempts.Single().Status.Should().Be(JobAttemptStatus.Succeeded);
        seen!.JobId.Should().Be(claimed.JobId);
        seen.AttemptNumber.Should().Be(1);
        seen.Payload.GetProperty("orderId").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task Permanent_failure_is_not_retried()
    {
        var (claimed, executor, services) = await ArrangeAsync((_, _) =>
            throw new PermanentJobFailureException("Order 7 does not exist."));
        await using var _ = services;

        await executor.ExecuteAsync(claimed, CancellationToken.None);

        var job = await fixture.GetJobAsync(claimed.JobId);
        job.Status.Should().Be(JobStatus.Failed);
        job.LastError.Should().Be("Order 7 does not exist.");
    }

    [Fact]
    public async Task Unexpected_exception_is_treated_as_transient_and_retried()
    {
        var (claimed, executor, services) = await ArrangeAsync((_, _) =>
            throw new InvalidOperationException("Downstream returned 503."), retryPolicy: new RetryPolicy(3, 30));
        await using var _ = services;

        await executor.ExecuteAsync(claimed, CancellationToken.None);

        var job = await fixture.GetJobAsync(claimed.JobId);
        job.Status.Should().Be(JobStatus.Pending);
        job.RetryCount.Should().Be(1);
        job.ScheduledAt.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(20));
        job.LastError.Should().Be("InvalidOperationException: Downstream returned 503.");
    }

    [Fact]
    public async Task Handler_that_ignores_cancellation_still_times_out()
    {
        var (claimed, executor, services) = await ArrangeAsync(
            (_, _) => Task.Delay(Timeout.Infinite, CancellationToken.None), timeoutSeconds: 1);
        await using var _ = services;

        var run = executor.ExecuteAsync(claimed, CancellationToken.None);

        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(run);
        var job = await fixture.GetJobAsync(claimed.JobId);
        job.Status.Should().Be(JobStatus.Pending);
        job.LastError.Should().Be("Timed out after 1 s.");
    }

    [Fact]
    public async Task Abort_records_a_transient_failure_so_another_worker_retries()
    {
        var (claimed, executor, services) = await ArrangeAsync((_, ct) => Task.Delay(Timeout.Infinite, ct));
        await using var _ = services;
        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await executor.ExecuteAsync(claimed, abort.Token);

        var job = await fixture.GetJobAsync(claimed.JobId);
        job.Status.Should().Be(JobStatus.Pending);
        job.LastError.Should().Be("The worker shut down before the job finished.");
    }

    [Fact]
    public async Task Result_for_a_job_cancelled_while_running_is_discarded()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var (claimed, executor, services) = await ArrangeAsync(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
        });
        await using var _ = services;

        var run = executor.ExecuteAsync(claimed, CancellationToken.None);
        await started.Task;
        await using (var scope = fixture.CreateScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(claimed.JobId);
            job!.Cancel(DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        release.SetResult();
        await run;

        var reloaded = await fixture.GetJobAsync(claimed.JobId);
        reloaded.Status.Should().Be(JobStatus.Cancelled);
        reloaded.Attempts.Single().Status.Should().Be(JobAttemptStatus.Cancelled);
    }

    [Fact]
    public async Task Job_without_a_handler_fails_permanently()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var job = await fixture.AddJobAsync(type);
        await using var services = fixture.BuildServices(s =>
        {
            s.AddSingleton<JobHandlerRegistry>(); // no handlers registered
            s.AddSingleton<JobExecutor>();
        });
        await using var scope = services.CreateAsyncScope();
        var claimed = (await scope.ServiceProvider.GetRequiredService<IJobClaimer>().ClaimAsync("w", [type], 1)).Single();

        await services.GetRequiredService<JobExecutor>().ExecuteAsync(claimed, CancellationToken.None);

        var reloaded = await fixture.GetJobAsync(job.Id);
        reloaded.Status.Should().Be(JobStatus.Failed);
        reloaded.LastError.Should().Contain("No handler");
    }
}

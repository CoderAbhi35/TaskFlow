using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Application.Abstractions;
using Reeve.Application.Execution;
using Reeve.Domain.Jobs;
using Reeve.Infrastructure.Persistence;
using Reeve.IntegrationTests.Persistence;

namespace Reeve.IntegrationTests.Worker;

[Collection(PostgresCollection.Name)]
public class JobEffectsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Side_effect_runs_once_even_when_later_steps_fail_and_the_job_is_retried()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var job = await fixture.AddJobAsync(type, retryPolicy: new RetryPolicy(3, 1));
        var emailsSent = 0;
        var handler = new DelegateJobHandler(type, async (ctx, ct) =>
        {
            await ctx.Effects.RunOnceAsync("send-email", _ =>
            {
                Interlocked.Increment(ref emailsSent);
                return Task.CompletedTask;
            }, ct);

            // A step after the email fails on the first two attempts.
            if (ctx.AttemptNumber < 3)
                throw new IOException("Audit service unavailable");
        });

        await using var services = fixture.BuildServices(s =>
        {
            s.AddSingleton<IJobHandler>(handler);
            s.AddSingleton<JobHandlerRegistry>();
            s.AddSingleton<JobExecutor>();
        });
        var executor = services.GetRequiredService<JobExecutor>();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // Wait out the retry backoff (1 s, then 3 s, with jitter).
            if ((await fixture.GetJobAsync(job.Id)).ScheduledAt is { } due && due > DateTimeOffset.UtcNow)
                await Task.Delay(due - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50));
            await using var scope = services.CreateAsyncScope();
            var claimed = await scope.ServiceProvider.GetRequiredService<IJobClaimer>().ClaimByIdAsync(job.Id, "w");
            claimed.Should().NotBeNull($"attempt {attempt} should be claimable");
            await executor.ExecuteAsync(claimed!, CancellationToken.None);
        }

        var reloaded = await fixture.GetJobAsync(job.Id);
        reloaded.Status.Should().Be(JobStatus.Succeeded);
        reloaded.AttemptCount.Should().Be(3);
        emailsSent.Should().Be(1);

        await using var check = fixture.CreateScope();
        var effect = await check.ServiceProvider.GetRequiredService<ReeveDbContext>().JobEffects.AsNoTracking()
            .SingleAsync(e => e.JobId == job.Id);
        effect.Key.Should().Be("send-email");
        effect.AttemptNumber.Should().Be(1);
    }

    [Fact]
    public async Task An_attempt_replaced_while_running_skips_its_side_effect()
    {
        // Attempt 1's worker looked dead (no heartbeats) but was still running. Recovery closed the
        // attempt and attempt 2 ran elsewhere. When attempt 1 reaches its side effect, it must not send.
        var type = await fixture.RegisterJobTypeAsync();
        var job = await fixture.AddJobAsync(type, retryPolicy: new RetryPolicy(3, 1));
        var emailsSent = 0;
        var firstAttemptMayContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondIsSending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateJobHandler(type, async (ctx, ct) =>
        {
            if (ctx.AttemptNumber == 1)
                await firstAttemptMayContinue.Task;
            await ctx.Effects.RunOnceAsync("send-email", async _ =>
            {
                Interlocked.Increment(ref emailsSent);
                if (ctx.AttemptNumber == 2)
                {
                    secondIsSending.SetResult();
                    await secondMayFinish.Task; // not recorded yet
                }
            }, ct);
        });

        await using var services = fixture.BuildServices(s =>
        {
            s.AddSingleton<IJobHandler>(handler);
            s.AddSingleton<JobHandlerRegistry>();
            s.AddSingleton<JobExecutor>();
        });
        var executor = services.GetRequiredService<JobExecutor>();

        ClaimedJob first;
        await using (var scope = services.CreateAsyncScope())
            first = (await scope.ServiceProvider.GetRequiredService<IJobClaimer>().ClaimByIdAsync(job.Id, "presumed-dead"))!;
        var firstRun = executor.ExecuteAsync(first, CancellationToken.None);

        // What recovery does: close attempt 1 as failed (retry now), then another worker claims it.
        await using (var scope = services.CreateAsyncScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(job.Id);
            loaded!.Fail("Worker stopped responding.", isTransient: true, DateTimeOffset.UtcNow.AddMinutes(-1), new Random(1), 1);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        ClaimedJob second;
        await using (var scope = services.CreateAsyncScope())
            second = (await scope.ServiceProvider.GetRequiredService<IJobClaimer>().ClaimByIdAsync(job.Id, "healthy"))!;
        second.AttemptNumber.Should().Be(2);

        // The race: attempt 2 is sending but hasn't recorded it, when attempt 1 reaches the same effect.
        // The "already recorded?" check can't see attempt 2's send; only the attempt check can stop 1.
        var secondRun = executor.ExecuteAsync(second, CancellationToken.None);
        await secondIsSending.Task;
        firstAttemptMayContinue.SetResult();
        await firstRun;
        secondMayFinish.SetResult();
        await secondRun;

        emailsSent.Should().Be(1, "only the attempt that owns the job sends");
        var reloaded = await fixture.GetJobAsync(job.Id);
        reloaded.Status.Should().Be(JobStatus.Succeeded, "attempt 1's late result is discarded");
        reloaded.Attempts.Single(a => a.AttemptNumber == 2).Status.Should().Be(JobAttemptStatus.Succeeded);
    }

    [Fact]
    public async Task A_job_cancelled_while_running_skips_its_side_effect()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var job = await fixture.AddJobAsync(type);
        var charged = 0;
        var mayContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateJobHandler(type, async (ctx, ct) =>
        {
            await mayContinue.Task;
            await ctx.Effects.RunOnceAsync("charge", _ =>
            {
                Interlocked.Increment(ref charged);
                return Task.CompletedTask;
            }, ct);
        });

        await using var services = fixture.BuildServices(s =>
        {
            s.AddSingleton<IJobHandler>(handler);
            s.AddSingleton<JobHandlerRegistry>();
            s.AddSingleton<JobExecutor>();
        });
        ClaimedJob claimed;
        await using (var scope = services.CreateAsyncScope())
            claimed = (await scope.ServiceProvider.GetRequiredService<IJobClaimer>().ClaimByIdAsync(job.Id, "w"))!;
        var run = services.GetRequiredService<JobExecutor>().ExecuteAsync(claimed, CancellationToken.None);

        await using (var scope = services.CreateAsyncScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(job.Id);
            loaded!.Cancel(DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        mayContinue.SetResult();
        await run;

        charged.Should().Be(0);
        (await fixture.GetJobAsync(job.Id)).Status.Should().Be(JobStatus.Cancelled);
    }

    [Fact]
    public async Task Recording_the_same_effect_twice_is_not_an_error()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var job = await fixture.AddJobAsync(type);

        await using var scope = fixture.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IJobEffectLedger>();
        await ledger.RecordAsync(job.Id, "charge", 1);
        await ledger.RecordAsync(job.Id, "charge", 2);

        (await ledger.IsRecordedAsync(job.Id, "charge")).Should().BeTrue();
        (await ledger.IsRecordedAsync(job.Id, "refund")).Should().BeFalse();
    }
}

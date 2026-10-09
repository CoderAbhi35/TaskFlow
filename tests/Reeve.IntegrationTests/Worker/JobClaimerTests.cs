using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Application.Abstractions;
using Reeve.Domain.Jobs;
using Reeve.IntegrationTests.Persistence;

namespace Reeve.IntegrationTests.Worker;

[Collection(PostgresCollection.Name)]
public class JobClaimerTests(PostgresFixture fixture)
{
    private async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string workerId, string[] types, int max)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IJobClaimer>().ClaimAsync(workerId, types, max);
    }

    [Fact]
    public async Task Claims_ready_jobs_by_priority_then_age_and_starts_an_attempt()
    {
        var type = await fixture.RegisterJobTypeAsync(timeoutSeconds: 42);
        var otherType = await fixture.RegisterJobTypeAsync();
        var oldLow = await fixture.AddJobAsync(type, JobPriority.Low);
        var high = await fixture.AddJobAsync(type, JobPriority.High);
        var normal = await fixture.AddJobAsync(type, JobPriority.Normal);
        var newerHigh = await fixture.AddJobAsync(type, JobPriority.High);
        await fixture.AddJobAsync(type, JobPriority.Critical, scheduledAt: DateTimeOffset.UtcNow.AddHours(1));
        await fixture.AddJobAsync(otherType, JobPriority.Critical);

        var claimed = await ClaimAsync("worker-a", [type], 10);

        claimed.Select(c => c.JobId).Should().Equal(high.Id, newerHigh.Id, normal.Id, oldLow.Id);
        claimed.Should().OnlyContain(c => c.AttemptNumber == 1 && c.Timeout == TimeSpan.FromSeconds(42) && c.JobType == type);

        var job = await fixture.GetJobAsync(high.Id);
        job.Status.Should().Be(JobStatus.Running);
        job.Attempts.Single().WorkerId.Should().Be("worker-a");
    }

    [Fact]
    public async Task Claims_at_most_the_requested_number()
    {
        var type = await fixture.RegisterJobTypeAsync();
        for (var i = 0; i < 5; i++)
            await fixture.AddJobAsync(type);

        (await ClaimAsync("w", [type], 2)).Should().HaveCount(2);
        (await ClaimAsync("w", [type], 10)).Should().HaveCount(3);
        (await ClaimAsync("w", [type], 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_job()
    {
        const int jobCount = 60;
        var type = await fixture.RegisterJobTypeAsync();
        for (var i = 0; i < jobCount; i++)
            await fixture.AddJobAsync(type);

        // All workers are released at once and pause briefly after each claim (as if processing), so
        // they genuinely contend for rows instead of one fast worker draining the queue alone.
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = new ConcurrentBag<(string Worker, Guid JobId)>();
        var workers = Enumerable.Range(1, 8).Select(n => Task.Run(async () =>
        {
            var workerId = $"worker-{n}";
            await startGate.Task;
            while (true)
            {
                var batch = await ClaimAsync(workerId, [type], 2);
                if (batch.Count == 0)
                    return;
                foreach (var job in batch)
                    claims.Add((workerId, job.JobId));
                await Task.Delay(10);
            }
        })).ToList();
        startGate.SetResult();
        await Task.WhenAll(workers);

        claims.Should().HaveCount(jobCount);
        claims.Select(c => c.JobId).Should().OnlyHaveUniqueItems();
        claims.Select(c => c.Worker).Distinct().Should().HaveCountGreaterThan(1, "the work should be shared");
    }

    [Fact]
    public async Task Claiming_by_ids_takes_the_claimable_jobs_in_one_go_and_skips_the_rest()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var running = await fixture.AddJobAsync(type);
        (await ClaimAsync("someone-else", [type], 1)).Single().JobId.Should().Be(running.Id);
        var ready = await fixture.AddJobAsync(type);
        var alsoReady = await fixture.AddJobAsync(type);
        var notDue = await fixture.AddJobAsync(type, scheduledAt: DateTimeOffset.UtcNow.AddHours(1));

        await using var scope = fixture.CreateScope();
        var claimed = await scope.ServiceProvider.GetRequiredService<IJobClaimer>()
            .ClaimByIdsAsync([alsoReady.Id, running.Id, notDue.Id, Guid.NewGuid(), ready.Id], "w");

        claimed.Select(c => c.JobId).Should().BeEquivalentTo([ready.Id, alsoReady.Id]);
        claimed.Should().OnlyContain(c => c.AttemptNumber == 1);
        (await fixture.GetJobAsync(running.Id)).Attempts.Should().ContainSingle(a => a.WorkerId == "someone-else");
        (await fixture.GetJobAsync(notDue.Id)).Status.Should().Be(JobStatus.Pending);
    }
}

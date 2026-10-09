using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Application.Abstractions;
using Reeve.Domain.Jobs;
using Reeve.Infrastructure.Persistence;
using Reeve.IntegrationTests.Persistence;

namespace Reeve.IntegrationTests.Scheduling;

[Collection(PostgresCollection.Name)]
public class QueuedJobSweeperTests(PostgresFixture fixture)
{
    private async Task<Job> AddQueuedJobAsync(string type, DateTimeOffset queuedAt)
    {
        var job = Job.Create(type, null, JobPriority.Normal, RetryPolicy.Default, queuedAt);
        job.MarkQueued(queuedAt);
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReeveDbContext>();
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    [Fact]
    public async Task Jobs_queued_for_too_long_go_back_to_pending_and_fresh_ones_stay()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var stuck = await AddQueuedJobAsync(type, DateTimeOffset.UtcNow.AddMinutes(-20));
        var fresh = await AddQueuedJobAsync(type, DateTimeOffset.UtcNow.AddSeconds(-5));

        await using var scope = fixture.CreateScope();
        var requeued = await scope.ServiceProvider.GetRequiredService<IQueuedJobSweeper>()
            .RequeueStaleAsync(TimeSpan.FromMinutes(10));

        requeued.Should().BeGreaterThanOrEqualTo(1);
        (await fixture.GetJobAsync(stuck.Id)).Status.Should().Be(JobStatus.Pending);
        (await fixture.GetJobAsync(fresh.Id)).Status.Should().Be(JobStatus.Queued);
    }
}

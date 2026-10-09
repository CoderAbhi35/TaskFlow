using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Application.Abstractions;
using Reeve.Contracts.Messages;
using Reeve.Domain.Jobs;
using Reeve.Infrastructure.Messaging;
using Reeve.IntegrationTests.Persistence;
using ContractJobs = Reeve.Contracts.Jobs;

namespace Reeve.IntegrationTests.Messaging;

/// <summary>Outbox behaviour with a fake publisher, so broker failures can be simulated precisely.</summary>
[Collection(PostgresCollection.Name)]
public class JobDispatcherTests(PostgresFixture fixture)
{
    private sealed class FakePublisher(Func<IReadOnlyList<JobDispatchMessage>, IReadOnlySet<Guid>> publish) : IJobPublisher
    {
        public List<JobDispatchMessage> Received { get; } = [];

        public List<OutgoingJob> ReceivedJobs { get; } = [];

        public Task<IReadOnlySet<Guid>> PublishAsync(IReadOnlyList<OutgoingJob> jobs, CancellationToken cancellationToken = default)
        {
            ReceivedJobs.AddRange(jobs);
            var messages = jobs.Select(j => j.Message).ToList();
            Received.AddRange(messages);
            return Task.FromResult(publish(messages));
        }
    }

    private async Task<int> DispatchAsync(FakePublisher publisher)
    {
        await using var services = fixture.BuildServices(s =>
        {
            s.AddReeveDispatcher(fixture.BuildConfiguration());
            s.AddSingleton<IJobPublisher>(publisher); // replaces the Kafka publisher
        });
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobDispatcher>().DispatchReadyJobsAsync(1000);
    }

    [Fact]
    public async Task Publishes_ready_jobs_and_marks_them_queued()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var ready = await fixture.AddJobAsync(type, JobPriority.High);
        var later = await fixture.AddJobAsync(type, scheduledAt: DateTimeOffset.UtcNow.AddHours(1));
        var publisher = new FakePublisher(messages => messages.Select(m => m.JobId).ToHashSet());

        await DispatchAsync(publisher);

        var message = publisher.Received.Single(m => m.JobId == ready.Id);
        message.JobType.Should().Be(type);
        message.Priority.Should().Be(ContractJobs.JobPriority.High);
        publisher.Received.Should().NotContain(m => m.JobId == later.Id, "scheduled jobs wait until they are due");

        (await fixture.GetJobAsync(ready.Id)).Status.Should().Be(JobStatus.Queued);
        (await fixture.GetJobAsync(later.Id)).Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task Jobs_stay_pending_when_publishing_fails()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var job = await fixture.AddJobAsync(type);
        var publisher = new FakePublisher(_ => throw new InvalidOperationException("broker unavailable"));

        var act = () => DispatchAsync(publisher);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await fixture.GetJobAsync(job.Id)).Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task Only_acknowledged_messages_mark_jobs_queued()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var delivered = await fixture.AddJobAsync(type);
        var undelivered = await fixture.AddJobAsync(type);
        var publisher = new FakePublisher(_ => new HashSet<Guid> { delivered.Id });

        await DispatchAsync(publisher);

        (await fixture.GetJobAsync(delivered.Id)).Status.Should().Be(JobStatus.Queued);
        (await fixture.GetJobAsync(undelivered.Id)).Status.Should().Be(JobStatus.Pending, "it is retried on the next pass");
    }
}

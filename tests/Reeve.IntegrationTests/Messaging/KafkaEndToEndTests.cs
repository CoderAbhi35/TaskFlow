using System.Collections.Concurrent;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reeve.Application;
using Reeve.Application.Execution;
using Reeve.Contracts.Messages;
using Reeve.Contracts.Serialization;
using Reeve.Domain.Jobs;
using Reeve.Infrastructure;
using Reeve.Infrastructure.Health;
using Reeve.Infrastructure.Messaging;
using Reeve.IntegrationTests.Persistence;
using Reeve.IntegrationTests.Worker;
using Reeve.Scheduler;
using Reeve.Worker;
using Reeve.Worker.Services;
using ContractJobs = Reeve.Contracts.Jobs;

namespace Reeve.IntegrationTests.Messaging;

/// <summary>
/// The full distributed path with real PostgreSQL and Kafka: database → dispatcher → Kafka → workers.
/// Each test uses its own job type (so its own topic) and its own consumer group.
/// </summary>
[Collection(KafkaCollection.Name)]
public class KafkaEndToEndTests(PostgresFixture postgres, KafkaFixture kafka)
{
    private readonly string _consumerGroup = $"test-group-{Guid.NewGuid():N}";

    private IHost BuildHost(IJobHandler? handler = null, bool withDispatcher = false, int concurrency = 4, string? deadLetterTopic = null,
        IJobHandler[]? handlers = null, string groupProtocol = "Consumer")
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(postgres.BuildConfiguration(new Dictionary<string, string?>
        {
            ["Kafka:GroupProtocol"] = groupProtocol,
            ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
            ["Kafka:ConsumerGroup"] = _consumerGroup,
            ["Kafka:Partitions"] = "6",
            ["Kafka:DeadLetterTopic"] = deadLetterTopic ?? "reeve.dead-letter",
            ["Worker:Transport"] = "Kafka",
            ["Worker:Concurrency"] = concurrency.ToString(),
            ["Worker:HeartbeatIntervalSeconds"] = "1",
            ["Worker:RecoveryIntervalSeconds"] = "60",
            ["Worker:ShutdownGraceSeconds"] = "5",
            ["Dispatcher:PollIntervalMs"] = "100",
        }));

        builder.Services.AddReeveApplication();
        builder.Services.AddReevePersistence(builder.Configuration);
        IJobHandler[] all = [.. handler is null ? [] : new[] { handler }, .. handlers ?? []];
        if (all.Length > 0)
        {
            builder.Services.AddReeveWorker(builder.Configuration);
            foreach (var h in all)
                builder.Services.AddSingleton(h);
        }
        if (withDispatcher)
        {
            builder.Services.AddReeveDispatcher(builder.Configuration);
            builder.Services.Configure<DispatcherOptions>(builder.Configuration.GetSection(DispatcherOptions.SectionName));
            builder.Services.AddHostedService<DispatcherService>();
        }

        return builder.Build();
    }

    private async Task<Job> WaitForAsync(Guid jobId, Func<Job, bool> condition, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var job = await postgres.GetJobAsync(jobId);
            if (condition(job))
                return job;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Job {jobId} is {job.Status}; the expected state was not reached.");
            await Task.Delay(100);
        }
    }

    private async Task PublishRawAsync(string type, params IEnumerable<string?> values)
    {
        using var producer = kafka.CreateProducer();
        foreach (var value in values)
            await producer.ProduceAsync($"reeve.jobs.{type}", new Message<string, string> { Key = "k", Value = value! });
    }

    private static string DispatchMessage(Job job) => JsonSerializer.Serialize(
        new JobDispatchMessage(job.Id, job.Type, ContractJobs.JobPriority.Normal, DateTimeOffset.UtcNow), ReeveJson.Options);

    [Fact]
    public async Task Job_flows_from_the_database_through_kafka_to_a_worker()
    {
        var type = await postgres.RegisterJobTypeAsync();
        var executed = new ConcurrentBag<Guid>();
        using var host = BuildHost(new DelegateJobHandler(type, (ctx, _) =>
        {
            executed.Add(ctx.JobId);
            return Task.CompletedTask;
        }), withDispatcher: true);
        await host.StartAsync();

        var job = await postgres.AddJobAsync(type);
        var done = await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);

        executed.Should().Equal(job.Id);
        done.Attempts.Should().ContainSingle();
        await host.StopAsync();
    }

    [Fact]
    public async Task One_trace_follows_the_job_from_creation_through_kafka_to_execution()
    {
        using var capture = new Telemetry.TelemetryCapture();
        var type = await postgres.RegisterJobTypeAsync();
        using var host = BuildHost(DelegateJobHandler.Succeeds(type), withDispatcher: true);
        await host.StartAsync();

        // As if created by an API request that was part of trace `traceId`.
        var traceId = System.Diagnostics.ActivityTraceId.CreateRandom();
        var job = Job.Create(type, null, JobPriority.Normal, RetryPolicy.Default, DateTimeOffset.UtcNow,
            traceParent: $"00-{traceId}-{System.Diagnostics.ActivitySpanId.CreateRandom()}-01");
        await using (var scope = postgres.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Reeve.Infrastructure.Persistence.ReeveDbContext>();
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        }

        await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);
        await host.StopAsync();

        var spans = capture.Spans.Where(s => Equals(s.GetTagItem("reeve.job.id"), job.Id)).ToList();
        var publish = spans.Single(s => s.Kind == System.Diagnostics.ActivityKind.Producer);
        var execute = spans.Single(s => s.Kind == System.Diagnostics.ActivityKind.Consumer);

        publish.TraceId.Should().Be(traceId);
        publish.GetTagItem("messaging.destination.name").Should().Be($"reeve.jobs.{type}");
        execute.TraceId.Should().Be(traceId, "the trace crosses the Kafka hop in the message headers");
        execute.ParentSpanId.Should().Be(publish.SpanId, "execution is a child of the publish span");
    }

    [Fact]
    public async Task A_backlog_claimed_in_batches_runs_each_job_once_and_commits_every_offset()
    {
        // Published before the worker starts, so the messages arrive together and are claimed several
        // per transaction. Afterwards nothing may be left to redeliver: offsets were stored in order.
        const int jobCount = 40;
        var type = await postgres.RegisterJobTypeAsync();
        using var host = BuildHost(new DelegateJobHandler(type, (_, ct) => Task.Delay(20, ct)), concurrency: 10);

        var jobs = new List<Job>();
        for (var i = 0; i < jobCount; i++)
            jobs.Add(await postgres.AddJobAsync(type));
        await PublishRawAsync(type, jobs.Select(DispatchMessage));
        await host.StartAsync();

        foreach (var job in jobs)
            await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);
        await host.StopAsync();

        foreach (var job in jobs)
            (await postgres.GetJobAsync(job.Id)).Attempts.Should().ContainSingle();

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        var committed = (await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(_consumerGroup, null)]))
            .Single().Partitions.Where(p => p.Topic == $"reeve.jobs.{type}").ToList();
        using var watermarks = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig { BootstrapServers = kafka.BootstrapServers, GroupId = "watermarks" }).Build();
        committed.Should().NotBeEmpty();
        foreach (var partition in committed)
            partition.Offset.Value.Should().Be(watermarks.QueryWatermarkOffsets(partition.TopicPartition, TimeSpan.FromSeconds(5)).High.Value,
                $"partition {partition.Partition.Value} is fully committed");
    }

    [Fact]
    public async Task Duplicate_messages_execute_the_job_only_once()
    {
        var type = await postgres.RegisterJobTypeAsync();
        var executions = 0;
        using var host = BuildHost(new DelegateJobHandler(type, async (_, _) =>
        {
            Interlocked.Increment(ref executions);
            await Task.Delay(200);
        }));
        await host.StartAsync();

        // No dispatcher: publish the same notification three times, as a crashed dispatcher might.
        var job = await postgres.AddJobAsync(type);
        await PublishRawAsync(type, DispatchMessage(job), DispatchMessage(job), DispatchMessage(job));

        var done = await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);
        await Task.Delay(1000); // give the duplicates time to be (not) processed

        executions.Should().Be(1);
        (await postgres.GetJobAsync(job.Id)).Attempts.Should().ContainSingle();
        await host.StopAsync();
    }

    [Fact]
    public async Task Unreadable_messages_are_dead_lettered_without_blocking_the_partition()
    {
        var type = await postgres.RegisterJobTypeAsync();
        var deadLetterTopic = $"test.dead-letter.{Guid.NewGuid():N}";
        using var host = BuildHost(DelegateJobHandler.Succeeds(type), deadLetterTopic: deadLetterTopic);
        await host.StartAsync();

        var job = await postgres.AddJobAsync(type);
        string?[] poison =
        [
            "not json",
            """{"jobId":"00000000-0000-0000-0000-000000000000"}""",
            "",
            """{"schemaVersion":99,"jobId":"0190a000-0000-7000-8000-000000000000"}""",
        ];
        await PublishRawAsync(type, [.. poison, DispatchMessage(job)]);

        await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);
        await host.StopAsync();

        // Every poison message is parked on the dead-letter topic, unchanged, with where it came from and why.
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = $"dlq-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(deadLetterTopic);
        var parked = new List<ConsumeResult<string, string>>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (parked.Count < poison.Length && DateTime.UtcNow < deadline)
        {
            if (consumer.Consume(TimeSpan.FromMilliseconds(500)) is { Message: not null } result)
                parked.Add(result);
        }

        parked.Select(p => p.Message.Value ?? "").Should().BeEquivalentTo(poison.Select(p => p ?? ""));
        foreach (var message in parked)
        {
            Header(message, KafkaDeadLetterPublisher.OriginalTopicHeader).Should().Be($"reeve.jobs.{type}");
            Header(message, KafkaDeadLetterPublisher.ReasonHeader).Should().NotBeNullOrWhiteSpace();
            Header(message, KafkaDeadLetterPublisher.OriginalOffsetHeader).Should().MatchRegex("^[0-9]+$");
        }

        static string? Header(ConsumeResult<string, string> message, string name) =>
            message.Message.Headers.TryGetLastBytes(name, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
    }

    [Fact]
    public async Task Saturated_worker_takes_no_more_work_than_its_concurrency_and_resumes()
    {
        var type = await postgres.RegisterJobTypeAsync();
        var running = 0;
        var maxRunning = 0;
        using var host = BuildHost(new DelegateJobHandler(type, async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(400);
            Interlocked.Decrement(ref running);
        }), withDispatcher: true, concurrency: 1);
        await host.StartAsync();

        var jobs = new List<Job>();
        for (var i = 0; i < 5; i++)
            jobs.Add(await postgres.AddJobAsync(type));
        foreach (var job in jobs)
            await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);

        maxRunning.Should().Be(1);
        await host.StopAsync();

        static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value
                   && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    [Fact]
    public async Task A_worker_busy_with_a_long_job_pauses_stays_in_the_group_and_resumes()
    {
        // Longer than the consumer waits for a slot before it pauses its partitions and polls.
        var type = await postgres.RegisterJobTypeAsync();
        var running = 0;
        var maxRunning = 0;
        var first = true;
        using var host = BuildHost(new DelegateJobHandler(type, async (_, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            if (now > Volatile.Read(ref maxRunning))
                Volatile.Write(ref maxRunning, now);
            var slow = Interlocked.Exchange(ref first, false);
            await Task.Delay(slow ? TimeSpan.FromSeconds(7) : TimeSpan.FromMilliseconds(50), ct);
            Interlocked.Decrement(ref running);
        }), withDispatcher: true, concurrency: 1);
        await host.StartAsync();
        await kafka.WaitForGroupAsync(_consumerGroup, members: 1, TimeSpan.FromSeconds(30));

        var jobs = new List<Job>();
        for (var i = 0; i < 4; i++)
            jobs.Add(await postgres.AddJobAsync(type));
        await Task.Delay(TimeSpan.FromSeconds(6));
        await kafka.WaitForGroupAsync(_consumerGroup, members: 1, TimeSpan.FromSeconds(5));
        foreach (var job in jobs)
            await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);

        maxRunning.Should().Be(1, "a worker with one slot never runs two jobs, even while it polls paused");
        foreach (var job in jobs)
            (await postgres.GetJobAsync(job.Id)).Attempts.Should().ContainSingle();
        await host.StopAsync();
    }

    [Fact]
    public async Task Workers_in_the_group_share_the_partitions_and_each_job_runs_once()
    {
        const int jobCount = 40;
        var type = await postgres.RegisterJobTypeAsync();
        var executions = new ConcurrentDictionary<Guid, int>();
        IJobHandler Handler() => new DelegateJobHandler(type, async (ctx, _) =>
        {
            executions.AddOrUpdate(ctx.JobId, 1, (_, n) => n + 1);
            await Task.Delay(50);
        });

        using var workerA = BuildHost(Handler());
        using var workerB = BuildHost(Handler());
        using var dispatcher = BuildHost(withDispatcher: true);
        await workerA.StartAsync();
        await workerB.StartAsync();
        await kafka.WaitForGroupAsync(_consumerGroup, members: 2, TimeSpan.FromSeconds(30));
        await dispatcher.StartAsync();

        var jobs = new List<Job>();
        for (var i = 0; i < jobCount; i++)
            jobs.Add(await postgres.AddJobAsync(type));
        foreach (var job in jobs)
            await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);

        executions.Should().HaveCount(jobCount).And.OnlyContain(e => e.Value == 1);
        var workerIds = new HashSet<string>();
        foreach (var job in jobs)
            workerIds.Add((await postgres.GetJobAsync(job.Id)).Attempts.Single().WorkerId);
        workerIds.Should().HaveCount(2, "both group members own partitions, so both receive work");

        await dispatcher.StopAsync();
        await workerA.StopAsync();
        await workerB.StopAsync();
    }

    [Fact]
    public async Task A_worker_whose_consumer_fails_fatally_replaces_it_and_recovers()
    {
        // A rolling upgrade from the classic group protocol: while an old worker still holds the group,
        // the broker refuses the new worker's consumer with a fatal error. Once the old worker is gone,
        // the new one must get going again by itself instead of polling a dead consumer forever.
        var type = await postgres.RegisterJobTypeAsync();
        var handler = new DelegateJobHandler(type, (_, _) => Task.CompletedTask);

        // Replace two old workers one at a time, the way a Deployment rolls.
        using var oldA = BuildHost(handler, groupProtocol: "Classic");
        using var oldB = BuildHost(handler, groupProtocol: "Classic");
        await oldA.StartAsync();
        await oldB.StartAsync();
        await kafka.WaitForGroupAsync(_consumerGroup, members: 2, TimeSpan.FromSeconds(30));

        using var newWorker = BuildHost(handler);
        using var newWorker2 = BuildHost(handler);
        using var dispatcher = BuildHost(withDispatcher: true);
        await newWorker.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));
        await oldA.StopAsync();
        await newWorker2.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));
        await oldB.StopAsync();
        await dispatcher.StartAsync();

        var jobs = new List<Job>();
        for (var i = 0; i < 6; i++)
            jobs.Add(await postgres.AddJobAsync(type));
        foreach (var job in jobs)
            await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded, timeoutSeconds: 60);

        // A consumer refused while old members remained is replaced every few seconds until one is let in.
        foreach (var host in new[] { newWorker, newWorker2 })
        {
            var monitor = host.Services.GetRequiredService<LoopMonitor>();
            DateTimeOffset? PolledAt() => monitor.Snapshot().Single(l => l.Name == KafkaJobConsumerService.LoopName).LastSuccess;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!(PolledAt() > DateTimeOffset.UtcNow.AddSeconds(-2)) && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(250);
            PolledAt().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), "every new worker ends up with a working consumer");
        }

        await dispatcher.StopAsync();
        await newWorker.StopAsync();
        await newWorker2.StopAsync();
    }

    [Fact]
    public async Task A_burst_of_one_job_type_reaches_every_worker_even_after_they_joined_one_at_a_time()
    {
        // Workers subscribe to a topic per job type. When they join one at a time (a scale-out), an
        // assignor that only balances each worker's total can hand the newcomer other topics and leave
        // all of the busy one where it was: adding workers would then add nothing for that job type.
        const int jobCount = 30;
        var busy = await postgres.RegisterJobTypeAsync();
        var quiet = await postgres.RegisterJobTypeAsync();
        var idle = await postgres.RegisterJobTypeAsync();
        IJobHandler[] Handlers() =>
        [
            new DelegateJobHandler(busy, (_, ct) => Task.Delay(50, ct)),
            new DelegateJobHandler(quiet, (_, _) => Task.CompletedTask),
            new DelegateJobHandler(idle, (_, _) => Task.CompletedTask),
        ];

        using var workerA = BuildHost(handlers: Handlers());
        using var workerB = BuildHost(handlers: Handlers());
        using var dispatcher = BuildHost(withDispatcher: true);
        await workerA.StartAsync();
        await kafka.WaitForGroupAsync(_consumerGroup, members: 1, TimeSpan.FromSeconds(30));
        await workerB.StartAsync();
        await kafka.WaitForGroupAsync(_consumerGroup, members: 2, TimeSpan.FromSeconds(30));
        var owners = await kafka.PartitionOwnersAsync(_consumerGroup, new KafkaOptions().TopicFor(busy));
        await dispatcher.StartAsync();

        var jobs = new List<Job>();
        for (var i = 0; i < jobCount; i++)
            jobs.Add(await postgres.AddJobAsync(busy));
        foreach (var job in jobs)
            await WaitForAsync(job.Id, j => j.Status == JobStatus.Succeeded);

        owners.Should().HaveCount(2, "each member owns part of every topic");
        var workerIds = new HashSet<string>();
        foreach (var job in jobs)
            workerIds.Add((await postgres.GetJobAsync(job.Id)).Attempts.Single().WorkerId);
        workerIds.Should().HaveCount(2, "the second worker adds capacity for the busy job type too");

        await dispatcher.StopAsync();
        await workerA.StopAsync();
        await workerB.StopAsync();
    }
}

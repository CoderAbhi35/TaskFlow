using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Reeve.Application.Abstractions;
using Reeve.Application.Execution;
using Reeve.Contracts.Messages;
using Reeve.Contracts.Serialization;
using Reeve.Infrastructure.Health;
using Reeve.Infrastructure.Messaging;

namespace Reeve.Worker.Services;

/// <summary>
/// Kafka transport (<c>Worker:Transport=Kafka</c>). Workers form one consumer group and subscribe to
/// the topics of the job types they can run, so Kafka spreads partitions across them.
/// </summary>
/// <remarks>
/// <para>
/// A message only says "job X is ready". The worker claims X in PostgreSQL and stores the offset as
/// soon as that claim is committed: from then on the database owns the job (a crash mid-execution is
/// handled by heartbeat-based recovery, not by redelivery). Offsets therefore advance in order and
/// quickly, while execution runs concurrently. If the claim itself fails, the offset is not stored and
/// the consumer seeks back, so the message is redelivered.
/// </para>
/// <para>
/// Batching: when more messages are already fetched and slots are free, up to one message per free
/// slot is claimed in a single transaction. A claim costs several database round trips, and claiming
/// one message at a time held each worker to one job in flight (measured under load).
/// </para>
/// <para>
/// Backpressure: a message is consumed only when a concurrency slot is free. While saturated the
/// consumer waits for a slot; if that takes more than a few seconds, it pauses the assigned
/// partitions and keeps polling, so it stays in the group instead of exceeding
/// <c>max.poll.interval.ms</c> and triggering a rebalance.
/// </para>
/// </remarks>
public sealed partial class KafkaJobConsumerService(
    WorkerIdentity identity,
    JobHandlerRegistry handlers,
    InFlightJobs inFlight,
    IServiceScopeFactory scopes,
    KafkaTopicManager topics,
    KafkaDeadLetterPublisher deadLetters,
    LoopMonitor loops,
    IOptions<KafkaOptions> kafkaOptions,
    ILogger<KafkaJobConsumerService> logger) : BackgroundService
{
    public const string LoopName = "Kafka consumer";

    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ClaimRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FatalRestartDelay = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for a free slot before polling Kafka with the partitions paused.</summary>
    private static readonly TimeSpan SaturatedWait = TimeSpan.FromSeconds(5);

    /// <summary>Most messages claimed in one transaction (also bounded by free slots).</summary>
    private const int MaxClaimBatch = 50;

    private readonly KafkaOptions _kafka = kafkaOptions.Value;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribing does not create topics; create them so the first job of a type is not missed.
        await topics.EnsureTopicsAsync(handlers.JobTypes, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        handlers.JobTypes.Count == 0
            ? Task.CompletedTask
            // Consume() blocks, so the loop gets a thread of its own rather than blocking host startup.
            : Task.Run(() => ConsumeLoopAsync(stoppingToken), CancellationToken.None);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await inFlight.DrainAsync(cancellationToken);
    }

    /// <summary>
    /// Runs consumers until shutdown. A fatal client error (the broker rejecting the group, a fenced
    /// member) leaves a consumer that will never deliver again, so it is replaced with a new one
    /// instead of being polled forever. Until the new one polls successfully the worker is not ready.
    /// </summary>
    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        loops.Register(LoopName, PollTimeout);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await ConsumeUntilFatalAsync(stoppingToken))
                    break;

                LogReplacingConsumer(FatalRestartDelay.TotalSeconds);
                await Task.Delay(FatalRestartDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            loops.Unregister(LoopName);
        }
    }

    /// <returns>True if the consumer failed fatally and must be replaced; false on shutdown.</returns>
    private async Task<bool> ConsumeUntilFatalAsync(CancellationToken stoppingToken)
    {
        var consumer = BuildConsumer();
        var fatal = false;
        try
        {
            consumer.Subscribe(handlers.JobTypes.Select(_kafka.TopicFor));
            var paused = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                // Every iteration polls Kafka (also while paused), so a quiet topic still beats.
                loops.Beat(LoopName);
                ConsumeResult<string, string>? result;
                var slot = false;
                try
                {
                    // All slots busy: wait for a job to finish, without touching Kafka at first.
                    // Pausing partitions discards the messages already fetched, so pausing whenever the
                    // slots were briefly full made every job wait for a new fetch (measured).
                    if (!inFlight.TryAcquire() && !await inFlight.AcquireAsync(SaturatedWait, stoppingToken))
                    {
                        // Busy for longer (slow jobs): poll with the partitions paused, so the consumer
                        // stays in the group and handles rebalances, then wait again.
                        paused = PauseWhileSaturated(consumer, paused);
                        loops.Succeeded(LoopName);
                        continue;
                    }

                    slot = true;
                    if (paused)
                    {
                        consumer.Resume(consumer.Assignment);
                        paused = false;
                    }

                    result = consumer.Consume(PollTimeout);
                    loops.Succeeded(LoopName);
                }
                catch (KafkaException ex)
                {
                    if (slot)
                        inFlight.Release();
                    if (ex.Error.IsFatal)
                    {
                        LogConsumerFatal(ex.Error.Reason);
                        fatal = true;
                        return true;
                    }
                    LogConsumeFailed(ex.Error.Reason);
                    continue;
                }

                if (result?.Message is null)
                {
                    inFlight.Release();
                    continue;
                }

                await HandleBatchAsync(consumer, TakeBatch(consumer, result), stoppingToken);
            }

            return false;
        }
        finally
        {
            try
            {
                // Commits stored offsets and leaves the group, so partitions are reassigned
                // immediately. A fatally failed consumer can't do either; just release it.
                if (!fatal)
                    consumer.Close();
            }
            catch (KafkaException ex)
            {
                LogConsumeFailed(ex.Error.Reason);
            }
            finally
            {
                consumer.Dispose();
            }
        }
    }

    /// <summary>
    /// Keeps polling (and so stays in the group, and handles rebalances) without taking messages while
    /// all slots are busy. Doesn't wait: the caller waits for a slot instead.
    /// </summary>
    private static bool PauseWhileSaturated(IConsumer<string, string> consumer, bool paused)
    {
        if (!paused)
            consumer.Pause(consumer.Assignment);

        var stray = consumer.Consume(TimeSpan.Zero);
        if (stray?.Message is not null)
        {
            // Arrived from a partition assigned after the pause: rewind it and pause that partition too.
            consumer.Seek(stray.TopicPartitionOffset);
            consumer.Pause([stray.TopicPartition]);
        }

        return true;
    }

    /// <summary>
    /// The first message plus any already fetched, one per free slot, without waiting for more.
    /// Every message returned holds a slot.
    /// </summary>
    private List<ConsumeResult<string, string>> TakeBatch(IConsumer<string, string> consumer, ConsumeResult<string, string> first)
    {
        var batch = new List<ConsumeResult<string, string>> { first };
        while (batch.Count < MaxClaimBatch && inFlight.TryAcquire())
        {
            ConsumeResult<string, string>? next;
            try
            {
                next = consumer.Consume(TimeSpan.Zero);
            }
            catch (KafkaException)
            {
                next = null; // the error comes back on the next regular poll, which handles it
            }

            if (next?.Message is null)
            {
                inFlight.Release();
                break;
            }
            batch.Add(next);
        }
        return batch;
    }

    /// <summary>
    /// Claims the batch's jobs in one transaction, then settles each message in order: start the job,
    /// skip it (duplicate or stale message), or park a poison message on the dead-letter topic. Called
    /// holding one slot per message; a slot is released unless a job is started with it.
    /// </summary>
    private async Task HandleBatchAsync(IConsumer<string, string> consumer, List<ConsumeResult<string, string>> batch, CancellationToken stoppingToken)
    {
        var parsed = batch.Select(r => (Result: r, Parsed: TryParse(r.Message.Value))).ToList();
        var jobIds = parsed.Where(p => p.Parsed.Message is not null).Select(p => p.Parsed.Message!.JobId).Distinct().ToList();

        Dictionary<Guid, ClaimedJob> claimed;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            claimed = (await scope.ServiceProvider.GetRequiredService<IJobClaimer>()
                .ClaimByIdsAsync(jobIds, identity.Id, stoppingToken)).ToDictionary(c => c.JobId);
        }
        catch (Exception ex)
        {
            // Nothing was decided: store no offsets and rewind each partition so the messages come again.
            inFlight.Release(batch.Count);
            foreach (var first in batch.GroupBy(r => r.TopicPartition).Select(g => g.MinBy(r => r.Offset.Value)!))
                consumer.Seek(first.TopicPartitionOffset);
            if (stoppingToken.IsCancellationRequested)
                return;

            LogClaimFailed(ex, jobIds.Count);
            await Task.Delay(ClaimRetryDelay, stoppingToken);
            return;
        }

        // A partition rewound to a poison message that couldn't be parked: later offsets in it must not
        // be stored, or the poison message would be skipped. Their jobs may still start (the claim is
        // durable); the redelivered messages are then skipped as duplicates.
        var rewound = new HashSet<TopicPartition>();
        foreach (var (result, (message, problem)) in parsed)
        {
            var job = message is not null && claimed.Remove(message.JobId, out var c) ? c : null;
            if (job is not null)
                // Continue the dispatch span carried in the message headers, when there is one.
                inFlight.Start(TraceParentFrom(result) is { } traceParent ? job with { TraceParent = traceParent } : job);
            else
                inFlight.Release();

            if (rewound.Contains(result.TopicPartition))
                continue;

            if (message is null)
            {
                // Poison message: it can never be processed. Park it on the dead-letter topic and move
                // on, rather than block the partition. Only skip it once it is safely parked.
                try
                {
                    await deadLetters.PublishAsync(result, problem!, identity.Id, stoppingToken);
                    LogDeadLettered(result.TopicPartitionOffset.ToString(), problem!);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogDeadLetterFailed(ex, result.TopicPartitionOffset.ToString());
                    consumer.Seek(result.TopicPartitionOffset);
                    rewound.Add(result.TopicPartition);
                    continue;
                }
            }
            else if (job is null)
            {
                LogSkipped(message.JobId);
            }

            // The message is settled (claimed, skipped as not claimable, or parked), so it's done.
            consumer.StoreOffset(result);
        }

        if (rewound.Count > 0)
            await Task.Delay(ClaimRetryDelay, stoppingToken);
    }

    private static string? TraceParentFrom(ConsumeResult<string, string> result) =>
        result.Message.Headers?.TryGetLastBytes(KafkaJobPublisher.TraceParentHeader, out var bytes) == true
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : null;

    private static (JobDispatchMessage? Message, string? Problem) TryParse(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return (null, "Empty message.");

        JobDispatchMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<JobDispatchMessage>(value, ReeveJson.Options);
        }
        catch (JsonException ex)
        {
            return (null, $"Not a valid dispatch message: {ex.Message}");
        }

        if (message is null)
            return (null, "Message deserialized to null.");
        if (message.SchemaVersion != JobDispatchMessage.CurrentSchemaVersion)
            return (null, $"Unsupported schema version {message.SchemaVersion}.");
        if (message.JobId == Guid.Empty)
            return (null, "Message has no job ID.");

        return (message, null);
    }

    private IConsumer<string, string> BuildConsumer() =>
        new ConsumerBuilder<string, string>(ConsumerConfigFor(_kafka, identity.Id))
        .SetPartitionsAssignedHandler((_, partitions) => LogAssigned(string.Join(", ", partitions)))
        .SetPartitionsRevokedHandler((_, partitions) => LogRevoked(string.Join(", ", partitions)))
        .SetErrorHandler((_, error) => LogKafkaError(error.Reason))
        .SetLogHandler((_, message) => KafkaLogging.Write(logger, message))
        .Build();

    internal static ConsumerConfig ConsumerConfigFor(KafkaOptions kafka, string clientId)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = kafka.ConsumerGroup,
            ClientId = clientId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // Offsets are stored explicitly once a claim is durable; auto-commit then commits them.
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
        };

        if (kafka.GroupProtocol == GroupProtocol.Consumer)
        {
            // Assignment and liveness move to the broker (its group.consumer.* settings). "range" gives
            // each member a share of every topic, so a burst of one job type reaches every worker; the
            // default ("uniform") balances only each member's total, like cooperative-sticky below.
            config.GroupProtocol = GroupProtocol.Consumer;
            config.GroupRemoteAssignor = "range";
        }
        else
        {
            config.PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky;
            config.SessionTimeoutMs = 10_000;
            config.HeartbeatIntervalMs = 3_000;
        }

        return config;
    }

    [LoggerMessage(LogLevel.Information, "Partitions assigned: {Partitions}")]
    private partial void LogAssigned(string partitions);

    [LoggerMessage(LogLevel.Information, "Partitions revoked: {Partitions}")]
    private partial void LogRevoked(string partitions);

    [LoggerMessage(LogLevel.Warning, "Kafka error: {Reason}")]
    private partial void LogKafkaError(string reason);

    [LoggerMessage(LogLevel.Warning, "Consuming failed: {Reason}")]
    private partial void LogConsumeFailed(string reason);

    [LoggerMessage(LogLevel.Error, "Kafka consumer failed fatally and can't recover: {Reason}")]
    private partial void LogConsumerFatal(string reason);

    [LoggerMessage(LogLevel.Warning, "Replacing the Kafka consumer in {Seconds} s")]
    private partial void LogReplacingConsumer(double seconds);

    [LoggerMessage(LogLevel.Error, "Unreadable message at {Position} moved to the dead-letter topic: {Problem}")]
    private partial void LogDeadLettered(string position, string problem);

    [LoggerMessage(LogLevel.Error, "Could not dead-letter the message at {Position}; it will be retried")]
    private partial void LogDeadLetterFailed(Exception ex, string position);

    [LoggerMessage(LogLevel.Error, "Claiming {Count} job(s) failed; the messages will be redelivered")]
    private partial void LogClaimFailed(Exception ex, int count);

    [LoggerMessage(LogLevel.Debug, "Job {JobId} is not claimable (duplicate, stale or already handled); message skipped")]
    private partial void LogSkipped(Guid jobId);
}

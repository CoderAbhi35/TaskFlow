using Confluent.Kafka;

namespace Reeve.Infrastructure.Messaging;

/// <summary>Configuration section <c>Kafka</c>.</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>Each job type gets its own topic: <c>{TopicPrefix}.{JobType}</c>.</summary>
    public string TopicPrefix { get; set; } = "reeve.jobs";

    public int Partitions { get; set; } = 6;

    public short ReplicationFactor { get; set; } = 1;

    public string ConsumerGroup { get; set; } = "reeve-workers";

    /// <summary>
    /// <c>Consumer</c> (Kafka 4.0+, KIP-848): the broker assigns partitions and rebalances one
    /// partition at a time, with the <c>range</c> assignor, which spreads every topic over all workers.
    /// <c>Classic</c> is for older brokers: the client-side cooperative-sticky assignor only balances
    /// each worker's total, so one topic can end up on a few workers, and adding workers adds no
    /// capacity for a busy job type.
    /// </summary>
    public GroupProtocol GroupProtocol { get; set; } = GroupProtocol.Consumer;

    /// <summary>Where messages that can never be processed are parked for inspection.</summary>
    public string DeadLetterTopic { get; set; } = "reeve.dead-letter";

    /// <summary>
    /// How long the producer keeps trying to deliver a message. Kept short so an unavailable broker
    /// surfaces quickly; undelivered jobs simply stay Pending and are retried on the next pass.
    /// </summary>
    public int DeliveryTimeoutMs { get; set; } = 10_000;

    public string TopicFor(string jobType) => $"{TopicPrefix}.{jobType}";
}

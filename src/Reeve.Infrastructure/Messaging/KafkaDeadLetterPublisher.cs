using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Reeve.Infrastructure.Messaging;

/// <summary>
/// Parks messages that can never be processed (unreadable, unknown schema) on the dead-letter topic,
/// byte-for-byte, with headers recording where they came from and why. The partition they came from
/// is then free to move on, and the message is still there for inspection or replay.
/// </summary>
/// <remarks>
/// Jobs that fail after exhausting their retries are a different case: they are already recorded as
/// DeadLettered in PostgreSQL, where they can be searched and retried through the API.
/// </remarks>
public sealed class KafkaDeadLetterPublisher : IDisposable
{
    public const string OriginalTopicHeader = "x-original-topic";
    public const string OriginalPartitionHeader = "x-original-partition";
    public const string OriginalOffsetHeader = "x-original-offset";
    public const string ReasonHeader = "x-dead-letter-reason";
    public const string SourceHeader = "x-dead-letter-source";

    private readonly IProducer<string?, string?> _producer;
    private readonly KafkaTopicManager _topics;
    private readonly KafkaOptions _options;

    public KafkaDeadLetterPublisher(IOptions<KafkaOptions> options, KafkaTopicManager topics, ILogger<KafkaDeadLetterPublisher> logger)
    {
        _options = options.Value;
        _topics = topics;
        _producer = new ProducerBuilder<string?, string?>(new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = _options.DeliveryTimeoutMs,
        })
        .SetLogHandler((_, message) => KafkaLogging.Write(logger, message))
        .Build();
    }

    /// <summary>Waits for the broker to acknowledge; throws if the message could not be parked.</summary>
    public async Task PublishAsync(ConsumeResult<string, string> original, string reason, string source, CancellationToken cancellationToken)
    {
        await _topics.EnsureTopicNamesAsync([_options.DeadLetterTopic], cancellationToken);

        var headers = new Headers
        {
            { OriginalTopicHeader, Encoding.UTF8.GetBytes(original.Topic) },
            { OriginalPartitionHeader, Encoding.UTF8.GetBytes(original.Partition.Value.ToString()) },
            { OriginalOffsetHeader, Encoding.UTF8.GetBytes(original.Offset.Value.ToString()) },
            { ReasonHeader, Encoding.UTF8.GetBytes(reason) },
            { SourceHeader, Encoding.UTF8.GetBytes(source) },
        };
        foreach (var header in original.Message.Headers ?? [])
            headers.Add(header.Key, header.GetValueBytes());

        await _producer.ProduceAsync(_options.DeadLetterTopic, new Message<string?, string?>
        {
            Key = original.Message.Key,
            Value = original.Message.Value,
            Headers = headers,
        }, cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}

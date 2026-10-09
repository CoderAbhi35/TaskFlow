using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Application.Abstractions;
using Reeve.Application.Telemetry;
using Reeve.Contracts.Messages;
using Reeve.Contracts.Serialization;

namespace Reeve.Infrastructure.Messaging;

/// <summary>
/// Idempotent producer with <c>acks=all</c>: a message counts as published only once every in-sync
/// replica has it. Keyed by job ID, which spreads jobs evenly across partitions.
/// </summary>
public sealed partial class KafkaJobPublisher : IJobPublisher, IDisposable
{
    /// <summary>W3C Trace Context header, as in OpenTelemetry's messaging conventions.</summary>
    public const string TraceParentHeader = "traceparent";

    private readonly IProducer<string, string> _producer;
    private readonly KafkaTopicManager _topics;
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaJobPublisher> _logger;

    public KafkaJobPublisher(IOptions<KafkaOptions> options, KafkaTopicManager topics, ILogger<KafkaJobPublisher> logger)
    {
        _options = options.Value;
        _topics = topics;
        _logger = logger;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = 5,
            MessageTimeoutMs = _options.DeliveryTimeoutMs,
            ClientId = $"reeve-dispatcher-{Environment.MachineName}".ToLowerInvariant(),
        })
        .SetLogHandler((_, message) => KafkaLogging.Write(logger, message))
        .Build();
    }

    public async Task<IReadOnlySet<Guid>> PublishAsync(
        IReadOnlyList<OutgoingJob> jobs, CancellationToken cancellationToken = default)
    {
        if (jobs.Count == 0)
            return new HashSet<Guid>();

        await _topics.EnsureTopicsAsync(jobs.Select(j => j.Message.JobType).Distinct(), cancellationToken);

        // Send the whole batch at once and wait for all acknowledgements; the producer batches them.
        var deliveries = jobs.Select(async job =>
        {
            var m = job.Message;
            var topic = _options.TopicFor(m.JobType);

            // A producer span in the job's own trace. Its context travels in the W3C traceparent
            // header, so the consumer continues the same trace.
            using var activity = ReeveTelemetry.StartContinuing($"{topic} publish", ActivityKind.Producer, job.TraceParent);
            activity?.SetTag("messaging.system", "kafka");
            activity?.SetTag("messaging.destination.name", topic);
            activity?.SetTag("reeve.job.id", m.JobId);

            var headers = new Headers();
            if ((activity?.Id ?? job.TraceParent) is { } traceParent)
                headers.Add(TraceParentHeader, Encoding.UTF8.GetBytes(traceParent));

            try
            {
                await _producer.ProduceAsync(topic, new Message<string, string>
                {
                    Key = m.JobId.ToString(),
                    Value = JsonSerializer.Serialize(m, ReeveJson.Options),
                    Headers = headers,
                }, cancellationToken);
                return (m.JobId, Delivered: true);
            }
            catch (ProduceException<string, string> ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Error.Reason);
                LogDeliveryFailed(m.JobId, ex.Error.Reason);
                return (m.JobId, Delivered: false);
            }
        }).ToList();

        var results = await Task.WhenAll(deliveries);
        return results.Where(r => r.Delivered).Select(r => r.JobId).ToHashSet();
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }

    [LoggerMessage(LogLevel.Warning, "Publishing job {JobId} failed: {Reason}. It stays Pending and will be retried.")]
    private partial void LogDeliveryFailed(Guid jobId, string reason);
}

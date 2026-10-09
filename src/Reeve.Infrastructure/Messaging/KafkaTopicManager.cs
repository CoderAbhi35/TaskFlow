using System.Collections.Concurrent;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Reeve.Infrastructure.Messaging;

/// <summary>
/// Creates job topics explicitly (with the configured partition count) instead of relying on broker
/// auto-creation, which would use broker defaults and doesn't happen for consumer subscriptions.
/// </summary>
public sealed partial class KafkaTopicManager(IOptions<KafkaOptions> options, ILogger<KafkaTopicManager> logger) : IDisposable
{
    private readonly KafkaOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, bool> _known = new(StringComparer.Ordinal);
    private readonly Lazy<IAdminClient> _admin = new(() =>
        new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.Value.BootstrapServers })
            .SetLogHandler((_, message) => KafkaLogging.Write(logger, message))
            .Build());

    public Task EnsureTopicsAsync(IEnumerable<string> jobTypes, CancellationToken cancellationToken = default) =>
        EnsureTopicNamesAsync(jobTypes.Select(_options.TopicFor), cancellationToken);

    public async Task EnsureTopicNamesAsync(IEnumerable<string> topicNames, CancellationToken cancellationToken = default)
    {
        var missing = topicNames.Where(t => !_known.ContainsKey(t)).Distinct().ToList();
        if (missing.Count == 0)
            return;

        try
        {
            await _admin.Value.CreateTopicsAsync(missing.Select(topic => new TopicSpecification
            {
                Name = topic,
                NumPartitions = _options.Partitions,
                ReplicationFactor = _options.ReplicationFactor,
            }));
            LogCreated(string.Join(", ", missing));
        }
        catch (CreateTopicsException ex)
        {
            var failed = ex.Results.Where(r => r.Error.Code is not (ErrorCode.NoError or ErrorCode.TopicAlreadyExists)).ToList();
            if (failed.Count > 0)
                throw new InvalidOperationException(
                    $"Could not create Kafka topics: {string.Join("; ", failed.Select(r => $"{r.Topic}: {r.Error.Reason}"))}", ex);
        }

        foreach (var topic in missing)
            _known[topic] = true;
    }

    public void Dispose()
    {
        if (_admin.IsValueCreated)
            _admin.Value.Dispose();
    }

    [LoggerMessage(LogLevel.Information, "Created Kafka topics: {Topics}")]
    private partial void LogCreated(string topics);
}

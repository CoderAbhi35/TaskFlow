using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Reeve.IntegrationTests.Persistence;
using Testcontainers.Kafka;

namespace Reeve.IntegrationTests.Messaging;

/// <summary>A disposable single-node Kafka broker (KRaft) for the Kafka test collection.</summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    // The same broker version as docker-compose and Kubernetes: Kafka 4 has the KIP-848 consumer protocol.
    private readonly KafkaContainer _container = new KafkaBuilder("apache/kafka:4.0.0").Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public IProducer<string, string> CreateProducer() =>
        new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = BootstrapServers }).Build();

    // Without a short timeout a describe request sent while the coordinator is loading hangs for the
    // default 60 s, which is longer than any wait below.
    private static readonly DescribeConsumerGroupsOptions DescribeOptions = new() { RequestTimeout = TimeSpan.FromSeconds(2) };

    /// <summary>Waits until the consumer group has settled with the expected number of members, each owning partitions.</summary>
    public async Task WaitForGroupAsync(string groupId, int members, TimeSpan timeout)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ConsumerGroupDescription group;
            try
            {
                group = (await admin.DescribeConsumerGroupsAsync([groupId], DescribeOptions)).ConsumerGroupDescriptions.Single();
            }
            catch (KafkaException)
            {
                // Kafka 4 reports a group that no member has joined yet as an error, not as an empty group, and a
                // fresh broker times the request out while its group coordinator is still loading.
                await Task.Delay(250);
                continue;
            }

            if (group.State == ConsumerGroupState.Stable
                && group.Members.Count == members
                && group.Members.All(m => m.Assignment.TopicPartitions.Count > 0))
                return;
            await Task.Delay(250);
        }

        throw new TimeoutException($"Consumer group {groupId} did not settle with {members} member(s).");
    }

    /// <summary>The group members (client IDs) that own at least one partition of the topic.</summary>
    public async Task<IReadOnlySet<string>> PartitionOwnersAsync(string groupId, string topic)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        var group = (await admin.DescribeConsumerGroupsAsync([groupId], DescribeOptions)).ConsumerGroupDescriptions.Single();
        return group.Members
            .Where(m => m.Assignment.TopicPartitions.Any(tp => tp.Topic == topic))
            .Select(m => m.ClientId)
            .ToHashSet();
    }
}

/// <summary>Tests that need both PostgreSQL and Kafka. Separate from the Postgres-only collection so it runs in parallel.</summary>
[CollectionDefinition(Name)]
public sealed class KafkaCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KafkaFixture>
{
    public const string Name = "kafka";
}

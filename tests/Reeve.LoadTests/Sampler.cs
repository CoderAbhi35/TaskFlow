using System.Diagnostics;
using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Npgsql;

namespace Reeve.LoadTests;

/// <summary>
/// Samples the system once a second while a run is in progress: backlog and running jobs, database
/// transactions per second, connections and lock waits, and Kafka consumer lag. With
/// <c>--docker-stats</c> it also records each container's CPU and memory.
/// </summary>
internal sealed class Sampler(Options options) : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly string _db = options.Get("db", Options.DefaultDb);
    private readonly string _kafka = options.Get("kafka", "localhost:9092");
    private readonly string _group = options.Get("group", "reeve-workers");
    private readonly bool _dockerStats = options.Flag("docker-stats");
    private readonly bool _kafkaLag = !options.Flag("no-kafka-lag");
    private DateTimeOffset _kafkaRetryAt;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Sample> _samples = [];
    private readonly Dictionary<string, ContainerPeak> _containers = [];
    private Task? _loop;
    private Task? _dockerLoop;

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_stop.Token));
        if (_dockerStats)
            _dockerLoop = Task.Run(() => DockerLoopAsync(_stop.Token));
    }

    public async Task<SampleResult> StopAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(new[] { _loop, _dockerLoop }.OfType<Task>());
        lock (_samples)
        {
            return new SampleResult
            {
                PeakBacklog = _samples.Count == 0 ? 0 : _samples.Max(s => s.Backlog),
                PeakRunning = _samples.Count == 0 ? 0 : _samples.Max(s => s.Running),
                PeakKafkaLag = _samples.Count == 0 ? 0 : _samples.Max(s => s.KafkaLag ?? 0),
                PeakDbTransactionsPerSecond = _samples.Count == 0 ? 0 : _samples.Max(s => s.DbTransactionsPerSecond),
                PeakDbConnections = _samples.Count == 0 ? 0 : _samples.Max(s => s.DbConnections),
                PeakDbLockWaits = _samples.Count == 0 ? 0 : _samples.Max(s => s.DbLockWaits),
                Containers = _containers.Values.OrderBy(c => c.Name).ToList(),
                Series = [.. _samples],
            };
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka }).Build();
        using var watermarks = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig
        {
            BootstrapServers = _kafka,
            GroupId = "reeve-load-test-watermarks",
        }).Build();
        long? previousCommits = null;
        var started = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            var tick = Stopwatch.StartNew();
            try
            {
                await using var connection = new NpgsqlConnection(_db);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("""
                    select
                      (select count(*) from jobs where status in ('Pending', 'Queued')),
                      (select count(*) from jobs where status = 'Running'),
                      (select sum(xact_commit + xact_rollback) from pg_stat_database where datname = current_database()),
                      (select count(*) from pg_stat_activity where datname = current_database()),
                      (select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock')
                    """, connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                var commits = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                var sample = new Sample
                {
                    Second = Math.Round(started.Elapsed.TotalSeconds, 1),
                    Backlog = reader.GetInt64(0),
                    Running = reader.GetInt64(1),
                    DbTransactionsPerSecond = previousCommits is { } p ? Math.Max(0, commits - p) : 0,
                    DbConnections = reader.GetInt64(3),
                    DbLockWaits = reader.GetInt64(4),
                    KafkaLag = await KafkaLagAsync(admin, watermarks),
                };
                previousCommits = commits;
                lock (_samples)
                    _samples.Add(sample);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break; // stopped mid-sample (e.g. while connecting): that's the end of the run
            }
            catch (Exception)
            {
                // The database may be restarting (a failure test): record nothing for this second.
                previousCommits = null;
            }

            var wait = Interval - tick.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Messages published but not yet consumed by the worker group, over all its partitions.</summary>
    /// <remarks>
    /// After an error (the broker restarting) the next query waits 5 s: querying librdkafka in a tight
    /// loop while the broker was down crashed this tool once (a native segfault). <c>--no-kafka-lag</c>
    /// turns sampling off entirely.
    /// </remarks>
    private async Task<long?> KafkaLagAsync(IAdminClient admin, IConsumer<Ignore, Ignore> watermarks)
    {
        if (!_kafkaLag || DateTimeOffset.UtcNow < _kafkaRetryAt)
            return null;
        try
        {
            var offsets = await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(_group, null)],
                new ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(2) });
            long lag = 0;
            foreach (var partition in offsets.Single().Partitions.Where(p => p.Topic.StartsWith("reeve.jobs.", StringComparison.Ordinal)))
            {
                var high = watermarks.QueryWatermarkOffsets(partition.TopicPartition, TimeSpan.FromSeconds(2)).High.Value;
                var committed = partition.Offset.Value < 0 ? 0 : partition.Offset.Value;
                lag += Math.Max(0, high - committed);
            }
            return lag;
        }
        catch (KafkaException)
        {
            _kafkaRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            return null; // broker restarting, or the group doesn't exist yet
        }
    }

    private async Task DockerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var psi = new ProcessStartInfo("docker", "stats --no-stream --format \"{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                using var process = Process.Start(psi)!;
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 3 || !parts[0].StartsWith("reeve", StringComparison.Ordinal))
                        continue;
                    var cpu = double.Parse(parts[1].TrimEnd('%'), CultureInfo.InvariantCulture);
                    var memory = ParseMiB(parts[2].Split('/')[0].Trim());
                    lock (_samples)
                    {
                        if (!_containers.TryGetValue(parts[0], out var peak))
                            _containers[parts[0]] = peak = new ContainerPeak { Name = parts[0] };
                        peak.PeakCpuPercent = Math.Max(peak.PeakCpuPercent, cpu);
                        peak.PeakMemoryMiB = Math.Max(peak.PeakMemoryMiB, memory);
                        peak.CpuSum += cpu;
                        peak.Samples++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // docker CLI unavailable: skip container stats.
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static double ParseMiB(string value)
    {
        var units = new (string Suffix, double Factor)[] { ("GiB", 1024), ("MiB", 1), ("KiB", 1.0 / 1024), ("B", 1.0 / 1024 / 1024) };
        foreach (var (suffix, factor) in units)
            if (value.EndsWith(suffix, StringComparison.Ordinal))
                return Math.Round(double.Parse(value[..^suffix.Length], CultureInfo.InvariantCulture) * factor, 1);
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested)
            await StopAsync();
        _stop.Dispose();
    }
}

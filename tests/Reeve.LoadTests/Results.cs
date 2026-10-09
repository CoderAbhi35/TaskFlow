using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.LoadTests;

internal sealed class RunResult
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required string RunId { get; init; }
    public required string Name { get; init; }
    public required string Command { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; set; }
    public IReadOnlyDictionary<string, string>? Settings { get; set; }
    public SubmissionResult? Submission { get; set; }
    public ExecutionResult? Execution { get; set; }
    public SampleResult? Samples { get; set; }
}

internal sealed class SubmissionResult
{
    public DateTimeOffset StartedAt { get; init; }
    public int Requests { get; init; }
    public double Seconds { get; init; }
    public double RequestsPerSecond { get; init; }
    public int Accepted { get; init; }
    public Dictionary<string, int> StatusCounts { get; init; } = [];
    public Latency? LatencyMs { get; init; }

    public double ErrorRatePercent => Requests == 0 ? 0 : Math.Round(100.0 * (Requests - Accepted) / Requests, 2);

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"Submission: {Requests} requests in {Seconds} s = {RequestsPerSecond} req/s | latency ms {LatencyMs} | errors {ErrorRatePercent}% {string.Join(" ", StatusCounts.Select(s => $"[{s.Key}]={s.Value}"))}");
}

internal sealed class ExecutionResult
{
    public long Jobs { get; init; }
    public long Succeeded { get; init; }
    public long Failed { get; init; }
    public long DeadLettered { get; init; }
    public long Retried { get; init; }
    public long RanMoreThanOnce { get; init; }
    public long Attempts { get; init; }
    public DateTimeOffset? FirstCreated { get; init; }
    public DateTimeOffset? LastCreated { get; init; }
    public DateTimeOffset? FirstStarted { get; init; }
    public DateTimeOffset? LastCompleted { get; init; }
    public double ExecutionJobsPerSecond { get; init; }
    public double EndToEndJobsPerSecond { get; init; }
    public Latency? EndToEndSeconds { get; init; }
    public double? EndToEndMaxSeconds { get; init; }
    public Latency? QueueWaitSeconds { get; init; }
    public Latency? ExecutionSeconds { get; init; }
    public long Effects { get; init; }
    public long EffectsOnJobsThatRanMoreThanOnce { get; init; }
    public bool TimedOut { get; set; }

    public string Summary() => string.Create(CultureInfo.InvariantCulture, $"""
        Execution: {Jobs} jobs: {Succeeded} succeeded, {Failed} failed, {DeadLettered} dead-lettered, {Retried} retried, {RanMoreThanOnce} ran more than once ({Attempts} attempts){(TimedOut ? " [TIMED OUT]" : "")}
          throughput {ExecutionJobsPerSecond} jobs/s from first start, {EndToEndJobsPerSecond} jobs/s from first submission
          end-to-end s {EndToEndSeconds} max {EndToEndMaxSeconds} | queue wait s {QueueWaitSeconds} | execution s {ExecutionSeconds}
          effects {Effects} ({EffectsOnJobsThatRanMoreThanOnce} on jobs that ran more than once)
        """);
}

internal sealed class SampleResult
{
    public long PeakBacklog { get; init; }
    public long PeakRunning { get; init; }
    public long PeakKafkaLag { get; init; }
    public long PeakDbTransactionsPerSecond { get; init; }
    public long PeakDbConnections { get; init; }
    public long PeakDbLockWaits { get; init; }
    public List<ContainerPeak> Containers { get; init; } = [];
    public List<Sample> Series { get; init; } = [];

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"Peaks: backlog {PeakBacklog}, running {PeakRunning}, Kafka lag {PeakKafkaLag}, DB {PeakDbTransactionsPerSecond} tx/s, {PeakDbConnections} connections, {PeakDbLockWaits} lock waits")
        + string.Concat(Containers.Select(c => string.Create(CultureInfo.InvariantCulture,
            $"\n  {c.Name,-28} cpu avg {c.AverageCpuPercent,6:0.0}% peak {c.PeakCpuPercent,6:0.0}%  mem peak {c.PeakMemoryMiB,7:0.0} MiB")));
}

internal sealed class Sample
{
    public double Second { get; init; }
    public long Backlog { get; init; }
    public long Running { get; init; }
    public long DbTransactionsPerSecond { get; init; }
    public long DbConnections { get; init; }
    public long DbLockWaits { get; init; }
    public long? KafkaLag { get; init; }
}

internal sealed class ContainerPeak
{
    public required string Name { get; init; }
    public double PeakCpuPercent { get; set; }
    public double PeakMemoryMiB { get; set; }
    [JsonIgnore] public double CpuSum { get; set; }
    [JsonIgnore] public int Samples { get; set; }
    public double AverageCpuPercent => Samples == 0 ? 0 : Math.Round(CpuSum / Samples, 1);
}

internal sealed record Latency(double P50, double P95, double P99, double? Max = null)
{
    public static Latency FromSeconds(double[] p) => new(Math.Round(p[0], 3), Math.Round(p[1], 3), Math.Round(p[2], 3));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"p50 {P50} p95 {P95} p99 {P99}{(Max is { } m ? $" max {m}" : "")}");
}

internal static class Percentiles
{
    /// <summary>Nearest-rank percentiles of an ascending array.</summary>
    public static Latency? Of(double[] sorted)
    {
        if (sorted.Length == 0)
            return null;
        double At(double q) => Math.Round(sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)], 1);
        return new Latency(At(0.50), At(0.95), At(0.99), Math.Round(sorted[^1], 1));
    }
}

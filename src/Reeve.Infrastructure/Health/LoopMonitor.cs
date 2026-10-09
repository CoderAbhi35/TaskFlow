using System.Collections.Concurrent;

namespace Reeve.Infrastructure.Health;

/// <summary>
/// Tracks the background loops of a worker or scheduler so probes can tell a stuck process (restart
/// it) from one whose dependencies are down (leave it alone). A loop beats after every pass, whether
/// the pass succeeded or failed, and reports successes separately.
/// </summary>
public sealed class LoopMonitor(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Loop> _loops = new();

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>Starts tracking a loop that completes a pass at least every <paramref name="interval"/>.</summary>
    public void Register(string name, TimeSpan interval) => _loops[name] = new Loop(interval, Now);

    /// <summary>A pass finished, successfully or not: the loop is not stuck.</summary>
    public void Beat(string name)
    {
        if (_loops.TryGetValue(name, out var loop))
            loop.Beat(Now, succeeded: false);
    }

    /// <summary>A pass finished and did its work.</summary>
    public void Succeeded(string name)
    {
        if (_loops.TryGetValue(name, out var loop))
            loop.Beat(Now, succeeded: true);
    }

    /// <summary>The loop ended on purpose (shutdown), so it no longer counts as stuck.</summary>
    public void Unregister(string name) => _loops.TryRemove(name, out _);

    public IReadOnlyList<LoopStatus> Snapshot() =>
        _loops.Select(l => l.Value.Status(l.Key)).OrderBy(l => l.Name, StringComparer.Ordinal).ToList();

    private sealed class Loop(TimeSpan interval, DateTimeOffset registeredAt)
    {
        private long _lastBeat = registeredAt.UtcTicks;
        private long _lastSuccess;

        public void Beat(DateTimeOffset now, bool succeeded)
        {
            Interlocked.Exchange(ref _lastBeat, now.UtcTicks);
            if (succeeded)
                Interlocked.Exchange(ref _lastSuccess, now.UtcTicks);
        }

        public LoopStatus Status(string name)
        {
            var success = Interlocked.Read(ref _lastSuccess);
            return new LoopStatus(name, interval,
                new DateTimeOffset(Interlocked.Read(ref _lastBeat), TimeSpan.Zero),
                success == 0 ? null : new DateTimeOffset(success, TimeSpan.Zero));
        }
    }
}

public sealed record LoopStatus(string Name, TimeSpan Interval, DateTimeOffset LastBeat, DateTimeOffset? LastSuccess)
{
    private static readonly TimeSpan MinimumStuckAfter = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Several missed passes, and at least a minute: a single pass may legitimately wait out a
    /// database or broker timeout before it fails.
    /// </summary>
    public TimeSpan StuckAfter => Interval * 5 > MinimumStuckAfter ? Interval * 5 : MinimumStuckAfter;
}

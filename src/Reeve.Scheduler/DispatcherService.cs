using Microsoft.Extensions.Options;
using Reeve.Application.Abstractions;
using Reeve.Infrastructure.Health;

namespace Reeve.Scheduler;

/// <summary>Configuration section <c>Dispatcher</c>.</summary>
public sealed class DispatcherOptions
{
    public const string SectionName = "Dispatcher";

    /// <summary>Jobs published per transaction.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>Wait between passes when the previous pass found fewer jobs than a full batch.</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>How often due recurring schedules are checked.</summary>
    public int SchedulePollIntervalMs { get; set; } = 1000;

    /// <summary>
    /// A job still Queued after this long is assumed to have lost its message and is dispatched again.
    /// Set it well above normal consumer lag; a re-dispatched duplicate is harmless but wasteful.
    /// </summary>
    public int RequeueQueuedAfterSeconds { get; set; } = 600;

    public int SweepIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Publishes jobs as they become ready: new jobs immediately, delayed jobs and retries once their
/// scheduled time arrives. A full batch means there is a backlog, so the next pass starts at once.
/// Several schedulers can run side by side; row locking keeps them from publishing the same job.
/// </summary>
public sealed partial class DispatcherService(
    IServiceScopeFactory scopes,
    LoopMonitor loops,
    IOptions<DispatcherOptions> options,
    ILogger<DispatcherService> logger) : LoopService(scopes, loops, logger)
{
    public const string LoopName = "Dispatcher";

    private readonly DispatcherOptions _options = options.Value;

    protected override string Name => LoopName;
    protected override TimeSpan IdleDelay => TimeSpan.FromMilliseconds(_options.PollIntervalMs);

    protected override async Task<bool> RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var dispatched = await services.GetRequiredService<IJobDispatcher>()
            .DispatchReadyJobsAsync(_options.BatchSize, cancellationToken);
        if (dispatched > 0)
            LogDispatched(dispatched);
        return dispatched >= _options.BatchSize;
    }

    [LoggerMessage(LogLevel.Information, "Dispatched {Count} job(s)")]
    private partial void LogDispatched(int count);
}

/// <summary>Creates a job for every due occurrence of a recurring schedule.</summary>
public sealed partial class ScheduleService(
    IServiceScopeFactory scopes,
    LoopMonitor loops,
    IOptions<DispatcherOptions> options,
    ILogger<ScheduleService> logger) : LoopService(scopes, loops, logger)
{
    private const int BatchSize = 100;

    protected override string Name => "Schedules";
    protected override TimeSpan IdleDelay => TimeSpan.FromMilliseconds(options.Value.SchedulePollIntervalMs);

    protected override async Task<bool> RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var fired = await services.GetRequiredService<IScheduleRunner>().FireDueSchedulesAsync(BatchSize, cancellationToken);
        if (fired > 0)
            LogFired(fired);
        return fired >= BatchSize;
    }

    [LoggerMessage(LogLevel.Information, "Fired {Count} schedule occurrence(s)")]
    private partial void LogFired(int count);
}

/// <summary>Re-dispatches jobs stuck in Queued (their message was lost or never consumed).</summary>
public sealed partial class QueuedJobSweeperService(
    IServiceScopeFactory scopes,
    LoopMonitor loops,
    IOptions<DispatcherOptions> options,
    ILogger<QueuedJobSweeperService> logger) : LoopService(scopes, loops, logger)
{
    protected override string Name => "Queued-job sweeper";
    protected override TimeSpan IdleDelay => TimeSpan.FromSeconds(options.Value.SweepIntervalSeconds);

    protected override async Task<bool> RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var requeued = await services.GetRequiredService<IQueuedJobSweeper>()
            .RequeueStaleAsync(TimeSpan.FromSeconds(options.Value.RequeueQueuedAfterSeconds), cancellationToken);
        if (requeued > 0)
            LogRequeued(requeued, options.Value.RequeueQueuedAfterSeconds);
        return false;
    }

    [LoggerMessage(LogLevel.Warning, "Re-dispatching {Count} job(s) that were Queued for over {Seconds} s without being claimed")]
    private partial void LogRequeued(int count, int seconds);
}

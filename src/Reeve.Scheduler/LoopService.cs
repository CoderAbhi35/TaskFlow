using Reeve.Infrastructure.Health;

namespace Reeve.Scheduler;

/// <summary>
/// A background loop that runs one pass at a time: immediately again when a pass reports more work,
/// otherwise after <see cref="IdleDelay"/>, and after <see cref="ErrorDelay"/> when a pass fails.
/// Every pass works on durable state in PostgreSQL, so a failed pass loses nothing. Each pass is
/// reported to the <see cref="LoopMonitor"/>, which backs the liveness and readiness probes.
/// </summary>
public abstract partial class LoopService(IServiceScopeFactory scopes, LoopMonitor loops, ILogger logger) : BackgroundService
{
    protected abstract string Name { get; }
    protected abstract TimeSpan IdleDelay { get; }
    protected virtual TimeSpan ErrorDelay => TimeSpan.FromSeconds(5);

    /// <returns>True if there is probably more work and the next pass should start at once.</returns>
    protected abstract Task<bool> RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(Name, IdleDelay.TotalMilliseconds);
        loops.Register(Name, IdleDelay > ErrorDelay ? IdleDelay : ErrorDelay);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                delay = await RunOnceAsync(scope.ServiceProvider, stoppingToken) ? TimeSpan.Zero : IdleDelay;
                loops.Succeeded(Name);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogPassFailed(ex, Name, ErrorDelay.TotalMilliseconds);
                loops.Beat(Name);
                delay = ErrorDelay;
            }

            if (delay <= TimeSpan.Zero)
                continue;

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(LogLevel.Information, "{Loop} started (idle interval {IntervalMs} ms)")]
    private partial void LogStarted(string loop, double intervalMs);

    [LoggerMessage(LogLevel.Error, "{Loop} pass failed; retrying in {DelayMs} ms")]
    private partial void LogPassFailed(Exception ex, string loop, double delayMs);
}

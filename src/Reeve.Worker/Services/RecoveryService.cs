using Microsoft.Extensions.Options;
using Reeve.Application.Abstractions;
using Reeve.Application.Workers;
using Reeve.Infrastructure.Health;

namespace Reeve.Worker.Services;

/// <summary>Periodically returns jobs held by crashed workers to the queue.</summary>
public sealed partial class RecoveryService(
    IServiceScopeFactory scopes,
    LoopMonitor loops,
    IOptions<WorkerOptions> options,
    IOptions<WorkerHealthOptions> health,
    TimeProvider time,
    ILogger<RecoveryService> logger) : BackgroundService
{
    private const string LoopName = "Recovery";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.RecoveryInterval, time);
        loops.Register(LoopName, options.Value.RecoveryInterval);
        try
        {
            do
            {
                await RecoverOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RecoverOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IJobRecovery>()
                .RecoverAsync(health.Value.HeartbeatTimeout, cancellationToken);

            if (result.WorkersMarkedOffline > 0 || result.JobsRecovered > 0)
                LogRecovered(result.WorkersMarkedOffline, result.JobsRecovered);
            loops.Succeeded(LoopName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecoveryFailed(ex);
            loops.Beat(LoopName);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Recovery: marked {Workers} stale worker(s) offline and returned {Jobs} job(s) to the queue")]
    private partial void LogRecovered(int workers, int jobs);

    [LoggerMessage(LogLevel.Error, "Recovery pass failed")]
    private partial void LogRecoveryFailed(Exception ex);
}

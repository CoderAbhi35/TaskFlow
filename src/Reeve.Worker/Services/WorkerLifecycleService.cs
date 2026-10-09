using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Reeve.Application.Abstractions;
using Reeve.Application.Execution;
using Reeve.Application.Telemetry;
using Reeve.Domain.Workers;
using Reeve.Infrastructure.Health;

namespace Reeve.Worker.Services;

/// <summary>
/// Registers the worker before any job is claimed, heartbeats while it runs, and marks it offline
/// after in-flight work has drained. Registered first, so it starts first and stops last.
/// </summary>
public sealed partial class WorkerLifecycleService(
    WorkerIdentity identity,
    JobHandlerRegistry handlers,
    InFlightJobs inFlight,
    IServiceScopeFactory scopes,
    LoopMonitor loops,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<WorkerLifecycleService> logger) : BackgroundService
{
    /// <summary>The worker is ready while this loop keeps succeeding: it is registered and can reach PostgreSQL.</summary>
    public const string LoopName = "Heartbeat";

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var worker = new KeyValuePair<string, object?>(ReeveTelemetry.WorkerTag, identity.Id);
        ReeveTelemetry.Meter.CreateObservableGauge("reeve.worker.active_jobs",
            () => new Measurement<int>(inFlight.Running, worker), "{job}", "Jobs this worker is executing right now.");
        ReeveTelemetry.Meter.CreateObservableGauge("reeve.worker.capacity",
            () => new Measurement<int>(inFlight.Capacity, worker), "{job}", "Jobs this worker can execute at once.");

        await RegisterAsync(cancellationToken);
        loops.Register(LoopName, options.Value.HeartbeatInterval);
        loops.Succeeded(LoopName);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var registry = scope.ServiceProvider.GetRequiredService<IWorkerRegistry>();
                    if (!await registry.HeartbeatAsync(identity.Id, stoppingToken))
                    {
                        LogReRegistering(identity.Id);
                        await RegisterAsync(stoppingToken);
                    }
                    loops.Succeeded(LoopName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep going: a missed heartbeat is survivable; a dead heartbeat loop is not.
                    LogHeartbeatFailed(ex);
                    loops.Beat(LoopName);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkerRegistry>()
                .SetStatusAsync(identity.Id, WorkerStatus.Offline, CancellationToken.None);
            LogStopped(identity.Id);
        }
        catch (Exception ex)
        {
            // Not fatal: recovery marks the worker offline once its heartbeat is stale.
            LogOfflineFailed(ex);
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var worker = WorkerNode.Register(identity.Id, identity.Hostname, options.Value.Concurrency,
            handlers.JobTypes, time.GetUtcNow());

        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkerRegistry>().RegisterAsync(worker, cancellationToken);
        LogRegistered(identity.Id, options.Value.Concurrency, string.Join(", ", handlers.JobTypes));
    }

    [LoggerMessage(LogLevel.Information, "Worker {WorkerId} registered: concurrency {Concurrency}, job types [{JobTypes}]")]
    private partial void LogRegistered(string workerId, int concurrency, string jobTypes);

    [LoggerMessage(LogLevel.Warning, "Worker {WorkerId} is no longer registered; registering again")]
    private partial void LogReRegistering(string workerId);

    [LoggerMessage(LogLevel.Error, "Heartbeat failed")]
    private partial void LogHeartbeatFailed(Exception ex);

    [LoggerMessage(LogLevel.Information, "Worker {WorkerId} is offline")]
    private partial void LogStopped(string workerId);

    [LoggerMessage(LogLevel.Warning, "Could not mark the worker offline")]
    private partial void LogOfflineFailed(Exception ex);
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Reeve.Application.Abstractions;
using Reeve.Application.Execution;
using Reeve.Domain.Workers;

namespace Reeve.Worker.Services;

/// <summary>
/// Concurrency slots and in-flight job tracking shared by both transports. A transport acquires a
/// slot before it claims a job, which is what gives the worker backpressure: it never takes on more
/// work than it can run.
/// </summary>
public sealed partial class InFlightJobs(
    WorkerIdentity identity,
    JobExecutor executor,
    IServiceScopeFactory scopes,
    IOptions<WorkerOptions> options,
    ILogger<InFlightJobs> logger) : IDisposable
{
    private readonly WorkerOptions _options = options.Value;
    private readonly SemaphoreSlim _slots = new(options.Value.Concurrency, options.Value.Concurrency);
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly CancellationTokenSource _abort = new();

    public int Capacity => _options.Concurrency;

    public int Running => _running.Count;

    /// <summary>Waits up to <paramref name="timeout"/> for a free slot; false if none became free.</summary>
    public Task<bool> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken) => _slots.WaitAsync(timeout, cancellationToken);

    public bool TryAcquire() => _slots.Wait(0);

    public void Release(int count = 1)
    {
        if (count > 0)
            _slots.Release(count);
    }

    /// <summary>Runs a claimed job on its own task. The caller's slot is released when it finishes.</summary>
    public void Start(ClaimedJob job)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await executor.ExecuteAsync(job, _abort.Token);
            }
            catch (Exception ex)
            {
                // The executor records outcomes itself, retrying for a while; reaching here means it
                // still failed. Recovery returns the job to the queue once the attempt is well past its
                // timeout (JobRecovery), even though this worker keeps heartbeating.
                LogExecutionFailed(ex, job.JobId);
            }
            finally
            {
                _slots.Release();
            }
        });

        _running[job.JobId] = task;
        task.ContinueWith(_ => _running.TryRemove(job.JobId, out Task? _), TaskScheduler.Default);
    }

    /// <summary>
    /// Call after the transport has stopped taking work: marks the worker Draining, gives in-flight
    /// jobs the grace period, then cancels the rest. Their outcome is recorded as a transient failure,
    /// so they go straight back to the queue instead of waiting for crash recovery.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        await MarkDrainingAsync();

        var running = _running.Values.ToArray();
        if (running.Length == 0)
            return;

        LogDraining(running.Length, _options.ShutdownGrace.TotalSeconds);
        try
        {
            await Task.WhenAll(running).WaitAsync(_options.ShutdownGrace, cancellationToken);
        }
        catch (TimeoutException)
        {
            LogAborting(_running.Count);
            await _abort.CancelAsync();
            await Task.WhenAll(_running.Values.ToArray()).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    public void Dispose()
    {
        _abort.Dispose();
        _slots.Dispose();
    }

    private async Task MarkDrainingAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkerRegistry>()
                .SetStatusAsync(identity.Id, WorkerStatus.Draining, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogDrainingFailed(ex);
        }
    }

    [LoggerMessage(LogLevel.Error, "Recording the outcome of job {JobId} failed")]
    private partial void LogExecutionFailed(Exception ex, Guid jobId);

    [LoggerMessage(LogLevel.Information, "Draining: waiting up to {GraceSeconds} s for {Count} in-flight job(s)")]
    private partial void LogDraining(int count, double graceSeconds);

    [LoggerMessage(LogLevel.Warning, "Grace period over: cancelling {Count} job(s); they will be retried")]
    private partial void LogAborting(int count);

    [LoggerMessage(LogLevel.Warning, "Could not mark the worker as draining")]
    private partial void LogDrainingFailed(Exception ex);
}

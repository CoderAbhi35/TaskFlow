using Microsoft.Extensions.Options;
using Reeve.Application.Abstractions;
using Reeve.Application.Execution;
using Reeve.Infrastructure.Health;

namespace Reeve.Worker.Services;

/// <summary>
/// Database transport (<c>Worker:Transport=Database</c>): waits for a free slot, claims as many ready
/// jobs as there are free slots with SKIP LOCKED, and backs off when there is no work. Needs nothing
/// but PostgreSQL, and serves as the baseline the Kafka transport is compared against.
/// </summary>
public sealed partial class JobProcessingService(
    WorkerIdentity identity,
    JobHandlerRegistry handlers,
    InFlightJobs inFlight,
    IServiceScopeFactory scopes,
    LoopMonitor loops,
    IOptions<WorkerOptions> options,
    ILogger<JobProcessingService> logger) : BackgroundService
{
    public const string LoopName = "Job polling";

    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (handlers.JobTypes.Count == 0)
        {
            LogNoHandlers();
            return;
        }

        loops.Register(LoopName, _options.PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait in steps, reporting in each time: all slots busy with long jobs is not a hang.
                while (!await inFlight.AcquireAsync(_options.PollInterval, stoppingToken))
                    loops.Beat(LoopName);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var free = 1;
            while (free < inFlight.Capacity && inFlight.TryAcquire())
                free++;

            IReadOnlyList<ClaimedJob> claimed;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                claimed = await scope.ServiceProvider.GetRequiredService<IJobClaimer>()
                    .ClaimAsync(identity.Id, handlers.JobTypes, free, stoppingToken);
            }
            catch (Exception ex)
            {
                inFlight.Release(free);
                if (stoppingToken.IsCancellationRequested)
                    break;

                LogClaimFailed(ex);
                loops.Beat(LoopName);
                await DelayAsync(_options.PollInterval, stoppingToken);
                continue;
            }

            loops.Succeeded(LoopName);

            inFlight.Release(free - claimed.Count);
            foreach (var job in claimed)
                inFlight.Start(job);

            if (claimed.Count == 0)
                await DelayAsync(WithJitter(_options.PollInterval), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop claiming first; then let in-flight jobs finish within the grace period.
        await base.StopAsync(cancellationToken);
        loops.Unregister(LoopName);
        await inFlight.DrainAsync(cancellationToken);
    }

    private static TimeSpan WithJitter(TimeSpan interval) =>
        interval * (0.8 + Random.Shared.NextDouble() * 0.4); // avoid workers polling in lock-step

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(LogLevel.Warning, "No job handlers are registered; this worker will not claim work")]
    private partial void LogNoHandlers();

    [LoggerMessage(LogLevel.Error, "Claiming jobs failed; retrying after the poll interval")]
    private partial void LogClaimFailed(Exception ex);
}

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reeve.Application.Abstractions;
using Reeve.Application.Jobs;
using Reeve.Application.Telemetry;
using Reeve.Domain;
using Reeve.Domain.Jobs;

namespace Reeve.Application.Execution;

/// <summary>
/// Runs one claimed job and records the outcome. Decides whether a failure is transient (retry) or
/// permanent, and enforces the job type's timeout even if a handler ignores cancellation.
/// </summary>
public sealed partial class JobExecutor(
    JobHandlerRegistry handlers,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<JobExecutor> logger)
{
    /// <summary>
    /// Tries to record an outcome, with a doubling pause (1, 2, 4, 8 s) after a failure. A database
    /// blip then doesn't strand the job; if recording still fails, recovery picks up the attempt once
    /// it is well past its timeout.
    /// </summary>
    private const int MaxRecordAttempts = 5;

    /// <param name="abortToken">Cancelled when the worker must stop now; the job is then retried elsewhere.</param>
    public async Task ExecuteAsync(ClaimedJob job, CancellationToken abortToken)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = job.JobId,
            ["JobType"] = job.JobType,
            ["AttemptNumber"] = job.AttemptNumber,
        });

        // Continues the job's trace (API request → dispatch → here), so one trace shows every attempt.
        using var activity = ReeveTelemetry.StartContinuing($"execute {job.JobType}", ActivityKind.Consumer, job.TraceParent);
        activity?.SetTag("reeve.job.id", job.JobId);
        activity?.SetTag("reeve.job.type", job.JobType);
        activity?.SetTag("reeve.job.attempt", job.AttemptNumber);

        var started = time.GetTimestamp();
        var outcome = await RunHandlerAsync(job, abortToken);
        var elapsed = time.GetElapsedTime(started);

        if (outcome.Succeeded)
            LogSucceeded(elapsed.TotalMilliseconds);
        else
            LogFailed(outcome.IsTransient, elapsed.TotalMilliseconds, outcome.Error!);

        // Recording must happen even while shutting down, so it is deliberately not cancellable.
        var recorded = await RecordAsync(job, outcome);
        var result = ResultName(outcome, recorded);

        activity?.SetTag("reeve.job.outcome", result);
        if (!outcome.Succeeded)
            activity?.SetStatus(ActivityStatusCode.Error, outcome.Error);
        Report(job.JobType, result, elapsed);
    }

    /// <summary>What happened to the job as a result of this attempt, as recorded in the database.</summary>
    private static string ResultName(Outcome outcome, JobStatus? recorded) => recorded switch
    {
        null => "discarded",
        JobStatus.Succeeded => "succeeded",
        JobStatus.Pending => "retried",
        JobStatus.DeadLettered => "dead_lettered",
        _ => outcome.Succeeded ? "succeeded" : "failed",
    };

    private static void Report(string jobType, string result, TimeSpan elapsed)
    {
        var type = new KeyValuePair<string, object?>(ReeveTelemetry.JobTypeTag, jobType);
        ReeveTelemetry.JobExecutionDuration.Record(elapsed.TotalSeconds, type, new(ReeveTelemetry.OutcomeTag, result));
        switch (result)
        {
            case "succeeded": ReeveTelemetry.JobsSucceeded.Add(1, type); break;
            case "retried": ReeveTelemetry.JobsRetried.Add(1, type); break;
            case "failed" or "dead_lettered": ReeveTelemetry.JobsFailed.Add(1, type, new(ReeveTelemetry.OutcomeTag, result)); break;
        }
    }

    private async Task<Outcome> RunHandlerAsync(ClaimedJob job, CancellationToken abortToken)
    {
        if (!handlers.TryGet(job.JobType, out var handler))
            return Outcome.Failure($"No handler is registered for job type '{job.JobType}'.", isTransient: false);

        using var timeout = new CancellationTokenSource(job.Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(abortToken, timeout.Token);

        try
        {
            var context = new JobExecutionContext(job.JobId, job.JobType, job.AttemptNumber, JobMappings.ParsePayload(job.Payload))
            {
                Effects = new JobEffects(scopes, job.JobId, job.AttemptNumber),
            };
            // WaitAsync stops waiting when cancelled even if the handler ignores its token.
            await handler.ExecuteAsync(context, linked.Token).WaitAsync(linked.Token);
            return Outcome.Success;
        }
        catch (OperationCanceledException) when (abortToken.IsCancellationRequested)
        {
            return Outcome.Failure("The worker shut down before the job finished.", isTransient: true);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Outcome.Failure($"Timed out after {job.Timeout.TotalSeconds:0.###} s.", isTransient: true);
        }
        catch (PermanentJobFailureException ex)
        {
            return Outcome.Failure(ex.Message, isTransient: false);
        }
        catch (Exception ex)
        {
            // Unknown failures are assumed transient: retrying is bounded by the retry policy, while
            // wrongly giving up on a recoverable job loses work.
            return Outcome.Failure($"{ex.GetType().Name}: {ex.Message}", isTransient: true);
        }
    }

    /// <returns>The job's status after recording, or null if the result was discarded.</returns>
    private async Task<JobStatus?> RecordAsync(ClaimedJob claimed, Outcome outcome)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                var job = await jobs.GetByIdAsync(claimed.JobId);
                if (job is null)
                {
                    LogDiscarded("the job no longer exists");
                    return null;
                }

                var now = time.GetUtcNow();
                if (outcome.Succeeded)
                    job.Succeed(now, claimed.AttemptNumber);
                else
                    job.Fail(outcome.Error!, outcome.IsTransient, now, Random.Shared, claimed.AttemptNumber);

                await unitOfWork.SaveChangesAsync();
                return job.Status;
            }
            catch (DomainException ex)
            {
                // Cancelled, or recovered and restarted elsewhere while this attempt was running.
                LogDiscarded(ex.Message);
                return null;
            }
            catch (ConcurrencyConflictException) when (attempt < MaxRecordAttempts)
            {
                // Someone changed the job in between (e.g. an operator cancelled it). Reload and re-decide.
            }
            catch (Exception ex) when (attempt < MaxRecordAttempts)
            {
                // The database is unreachable or failed: wait and try again. If the save did commit and
                // only the reply was lost, the reload sees the new status and the result is discarded.
                var delay = TimeSpan.FromSeconds(1 << (attempt - 1));
                LogRecordRetry(ex, attempt, delay.TotalSeconds);
                await Task.Delay(delay, time);
            }
        }
    }

    private readonly record struct Outcome(bool Succeeded, string? Error, bool IsTransient)
    {
        public static Outcome Success => new(true, null, false);
        public static Outcome Failure(string error, bool isTransient) => new(false, error, isTransient);
    }

    [LoggerMessage(LogLevel.Information, "Job succeeded in {ElapsedMs:0} ms")]
    private partial void LogSucceeded(double elapsedMs);

    [LoggerMessage(LogLevel.Warning, "Job failed (transient: {IsTransient}) after {ElapsedMs:0} ms: {Error}")]
    private partial void LogFailed(bool isTransient, double elapsedMs, string error);

    [LoggerMessage(LogLevel.Warning, "Recording the result failed (try {Attempt}); retrying in {DelaySeconds} s")]
    private partial void LogRecordRetry(Exception ex, int attempt, double delaySeconds);

    [LoggerMessage(LogLevel.Warning, "Result discarded: {Reason}")]
    private partial void LogDiscarded(string reason);
}

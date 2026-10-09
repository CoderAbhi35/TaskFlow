using System.Text.RegularExpressions;

namespace Reeve.Domain.Jobs;

/// <summary>
/// Aggregate root for a unit of asynchronous work. PostgreSQL holds the authoritative copy of this
/// state; the transport only carries the job ID. All state changes go through the methods below so
/// that invalid transitions (e.g. a duplicate delivery trying to complete a cancelled job) are rejected.
/// </summary>
/// <remarks>
/// Lifecycle:
/// <code>
/// Pending ──► Queued ──► Running ──► Succeeded
///    ▲ │         │         │ ├──► Failed        (permanent error)
///    │ │         │         │ ├──► DeadLettered  (transient errors, retries exhausted)
///    │ │         │         │ └──► Pending       (transient error, retry scheduled)
///    │ └─────────┴─────────┴────► Cancelled
///    └──── Failed / DeadLettered  (manual retry)
/// </code>
/// </remarks>
public sealed partial class Job
{
    public const int MaxTypeLength = 100;
    public const int MaxIdempotencyKeyLength = 200;
    public const int MaxIdempotencyScopeLength = 200;
    public const int MaxErrorLength = 4000;
    public const int MaxIdempotencyFingerprintLength = 64;
    public const int MaxTraceParentLength = 128;

    private readonly List<JobAttempt> _attempts = [];

    public Guid Id { get; private set; }
    public string Type { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public JobStatus Status { get; private set; }
    public JobPriority Priority { get; private set; }
    public int MaxRetries { get; private set; }
    public int BackoffSeconds { get; private set; }
    public string? IdempotencyKey { get; private set; }

    /// <summary>
    /// Whose key <see cref="IdempotencyKey"/> is: the authenticated caller, or "system" for jobs the
    /// platform creates. Keys are unique per scope, so one client can't collide with, or look up,
    /// another client's job by guessing its key.
    /// </summary>
    public string? IdempotencyScope { get; private set; }

    /// <summary>
    /// Hash of the request that created the job under <see cref="IdempotencyKey"/>. Lets a replay of the
    /// same request be told apart from a client reusing the key for a different request.
    /// </summary>
    public string? IdempotencyFingerprint { get; private set; }

    /// <summary>Earliest time the job may run. Null means as soon as possible.</summary>
    public DateTimeOffset? ScheduledAt { get; private set; }

    /// <summary>
    /// W3C traceparent of the request (or schedule run) that created the job. Dispatch and execution
    /// happen later in other processes; they continue this trace, so one trace follows the job.
    /// </summary>
    public string? TraceParent { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Total executions started, including manual retries. Used to number attempts.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Automatic retries consumed against <see cref="MaxRetries"/>. Reset by a manual retry.</summary>
    public int RetryCount { get; private set; }

    public string? LastError { get; private set; }

    public IReadOnlyList<JobAttempt> Attempts => _attempts;
    public RetryPolicy RetryPolicy => new(MaxRetries, BackoffSeconds);
    public JobAttempt? CurrentAttempt => _attempts.LastOrDefault(a => a.Status == JobAttemptStatus.Running);

    private Job() { } // EF Core

    public static Job Create(
        string type,
        string? payload,
        JobPriority priority,
        RetryPolicy retryPolicy,
        DateTimeOffset now,
        DateTimeOffset? scheduledAt = null,
        string? idempotencyKey = null,
        string? idempotencyFingerprint = null,
        string? traceParent = null,
        string idempotencyScope = "")
    {
        ArgumentNullException.ThrowIfNull(retryPolicy);

        type = type?.Trim() ?? "";
        if (type.Length is 0 or > MaxTypeLength || !JobTypePattern().IsMatch(type))
            throw new DomainException(
                $"Job type must be 1-{MaxTypeLength} characters of letters, digits, '_', '.' or '-'.");

        if (!Enum.IsDefined(priority))
            throw new DomainException($"Unknown priority '{priority}'.");

        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (idempotencyKey?.Length > MaxIdempotencyKeyLength)
            throw new DomainException($"Idempotency key must be at most {MaxIdempotencyKeyLength} characters.");
        if (idempotencyKey is null && idempotencyFingerprint is not null)
            throw new DomainException("An idempotency fingerprint requires an idempotency key.");
        if (idempotencyScope is null || idempotencyScope.Length > MaxIdempotencyScopeLength)
            throw new DomainException($"Idempotency scope must be at most {MaxIdempotencyScopeLength} characters.");
        if (idempotencyFingerprint?.Length > MaxIdempotencyFingerprintLength)
            throw new DomainException($"Idempotency fingerprint must be at most {MaxIdempotencyFingerprintLength} characters.");

        now = now.ToUniversalTime();
        return new Job
        {
            Id = Guid.CreateVersion7(now),
            Type = type,
            Payload = string.IsNullOrWhiteSpace(payload) ? "{}" : payload,
            Status = JobStatus.Pending,
            Priority = priority,
            MaxRetries = retryPolicy.MaxRetries,
            BackoffSeconds = retryPolicy.BackoffSeconds,
            IdempotencyKey = idempotencyKey,
            IdempotencyScope = idempotencyKey is null ? null : idempotencyScope,
            IdempotencyFingerprint = idempotencyFingerprint,
            TraceParent = traceParent?.Length <= MaxTraceParentLength ? traceParent : null,
            ScheduledAt = scheduledAt?.ToUniversalTime(),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public bool IsReadyToRun(DateTimeOffset now) =>
        Status == JobStatus.Pending && (ScheduledAt is null || ScheduledAt <= now);

    /// <summary>Records that the job has been handed to the transport.</summary>
    public void MarkQueued(DateTimeOffset now)
    {
        EnsureStatus("be queued", JobStatus.Pending);
        if (!IsReadyToRun(now))
            throw new DomainException($"Job {Id} is scheduled for {ScheduledAt:O} and cannot be queued yet.");

        Status = JobStatus.Queued;
        Touch(now);
    }

    /// <summary>
    /// Returns a Queued job whose dispatch message was apparently never consumed to Pending, so it is
    /// dispatched again. Safe even if the original message turns up later: only one claim can succeed.
    /// </summary>
    public void ReturnToPending(DateTimeOffset now)
    {
        EnsureStatus("return to pending", JobStatus.Queued);

        Status = JobStatus.Pending;
        Touch(now);
    }

    /// <summary>
    /// Claims the job for a worker and opens a new attempt. Allowed from Pending as well as Queued so a
    /// database-polling worker can claim directly without going through the transport.
    /// </summary>
    public JobAttempt Start(string workerId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new DomainException("Worker ID is required to start a job.");
        EnsureStatus("start", JobStatus.Pending, JobStatus.Queued);

        now = now.ToUniversalTime();
        AttemptCount++;
        var attempt = new JobAttempt(Id, AttemptCount, workerId, now);
        _attempts.Add(attempt);

        Status = JobStatus.Running;
        Touch(now);
        return attempt;
    }

    /// <param name="attemptNumber">
    /// The attempt the caller is reporting on. Pass it from workers so that a late report for an attempt
    /// that was already recovered (and possibly restarted elsewhere) is rejected rather than applied.
    /// </param>
    public void Succeed(DateTimeOffset now, int? attemptNumber = null)
    {
        EnsureStatus("succeed", JobStatus.Running);
        EnsureCurrentAttempt(attemptNumber);

        now = now.ToUniversalTime();
        CurrentAttempt!.Close(JobAttemptStatus.Succeeded, now);
        Status = JobStatus.Succeeded;
        LastError = null;
        CompletedAt = now;
        Touch(now);
    }

    /// <summary>
    /// Records a failed attempt. Transient failures are retried with backoff until the retry policy is
    /// exhausted, after which the job is dead-lettered. Permanent failures are never retried.
    /// </summary>
    /// <param name="random">Jitter source for the retry delay.</param>
    /// <param name="attemptNumber">The attempt being reported on; see <see cref="Succeed"/>.</param>
    public void Fail(string error, bool isTransient, DateTimeOffset now, Random random, int? attemptNumber = null)
    {
        EnsureStatus("fail", JobStatus.Running);
        EnsureCurrentAttempt(attemptNumber);

        now = now.ToUniversalTime();
        error = Truncate(string.IsNullOrWhiteSpace(error) ? "Unknown error." : error, MaxErrorLength);
        CurrentAttempt!.Close(JobAttemptStatus.Failed, now, error);
        LastError = error;

        if (!isTransient)
        {
            Status = JobStatus.Failed;
            CompletedAt = now;
        }
        else if (RetryCount < MaxRetries)
        {
            RetryCount++;
            Status = JobStatus.Pending;
            ScheduledAt = now + RetryPolicy.GetDelay(RetryCount, random);
        }
        else
        {
            Status = JobStatus.DeadLettered;
            CompletedAt = now;
        }

        Touch(now);
    }

    /// <summary>
    /// Cancels a job that has not completed. A running attempt is closed as cancelled. The worker is
    /// not interrupted: its handler runs on, but skips any side effect it hasn't done yet (the effects
    /// ledger checks the attempt is still open), and the result it reports is rejected.
    /// </summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status.IsTerminal())
            throw new InvalidJobStateTransitionException(Id, Status, "be cancelled");

        now = now.ToUniversalTime();
        CurrentAttempt?.Close(JobAttemptStatus.Cancelled, now, "Cancelled.");
        Status = JobStatus.Cancelled;
        CompletedAt = now;
        Touch(now);
    }

    /// <summary>Operator-requested retry of a failed or dead-lettered job, with a fresh retry budget.</summary>
    public void Requeue(DateTimeOffset now)
    {
        EnsureStatus("be retried", JobStatus.Failed, JobStatus.DeadLettered);

        Status = JobStatus.Pending;
        RetryCount = 0;
        ScheduledAt = null;
        CompletedAt = null;
        Touch(now);
    }

    private void EnsureStatus(string action, params ReadOnlySpan<JobStatus> allowed)
    {
        foreach (var status in allowed)
        {
            if (Status == status)
                return;
        }

        throw new InvalidJobStateTransitionException(Id, Status, action);
    }

    private void EnsureCurrentAttempt(int? attemptNumber)
    {
        if (attemptNumber is { } expected && CurrentAttempt?.AttemptNumber != expected)
            throw new StaleAttemptException(Id, expected, CurrentAttempt!.AttemptNumber);
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = now.ToUniversalTime();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex JobTypePattern();
}

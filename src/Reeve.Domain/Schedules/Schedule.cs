using System.Text.RegularExpressions;
using Reeve.Domain.Jobs;

namespace Reeve.Domain.Schedules;

/// <summary>
/// A recurring job template: on every cron occurrence the scheduler creates a new <see cref="Job"/>
/// from it. Cron evaluation lives outside the domain; the schedule only records when it is due next.
/// </summary>
public sealed partial class Schedule
{
    public const int MaxNameLength = 100;
    public const int MaxCronLength = 100;
    public const int MaxTimeZoneLength = 64;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string JobType { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public JobPriority Priority { get; private set; }
    public int MaxRetries { get; private set; }
    public int BackoffSeconds { get; private set; }
    public string CronExpression { get; private set; } = null!;
    public string TimeZone { get; private set; } = null!;
    public bool Enabled { get; private set; }

    /// <summary>The next occurrence to fire. Null when the expression has no future occurrences.</summary>
    public DateTimeOffset? NextRunAt { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }
    public Guid? LastJobId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public RetryPolicy RetryPolicy => new(MaxRetries, BackoffSeconds);

    private Schedule() { } // EF Core

    public static Schedule Create(
        string name,
        string jobType,
        string? payload,
        JobPriority priority,
        RetryPolicy retryPolicy,
        string cronExpression,
        string timeZone,
        DateTimeOffset? firstRunAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(retryPolicy);

        name = name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength || !NamePattern().IsMatch(name))
            throw new DomainException(
                $"Schedule name must be 1-{MaxNameLength} characters of letters, digits, spaces, '_', '.' or '-'.");
        if (string.IsNullOrWhiteSpace(jobType) || jobType.Length > Job.MaxTypeLength)
            throw new DomainException("A schedule needs a job type.");
        if (string.IsNullOrWhiteSpace(cronExpression) || cronExpression.Length > MaxCronLength)
            throw new DomainException($"Cron expression must be 1-{MaxCronLength} characters.");
        if (string.IsNullOrWhiteSpace(timeZone) || timeZone.Length > MaxTimeZoneLength)
            throw new DomainException($"Time zone must be 1-{MaxTimeZoneLength} characters.");
        if (!Enum.IsDefined(priority))
            throw new DomainException($"Unknown priority '{priority}'.");

        now = now.ToUniversalTime();
        return new Schedule
        {
            Id = Guid.CreateVersion7(now),
            Name = name,
            JobType = jobType.Trim(),
            Payload = string.IsNullOrWhiteSpace(payload) ? "{}" : payload,
            Priority = priority,
            MaxRetries = retryPolicy.MaxRetries,
            BackoffSeconds = retryPolicy.BackoffSeconds,
            CronExpression = cronExpression.Trim(),
            TimeZone = timeZone.Trim(),
            Enabled = true,
            NextRunAt = firstRunAt?.ToUniversalTime(),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public bool IsDue(DateTimeOffset now) => Enabled && NextRunAt <= now;

    /// <summary>
    /// Records that the occurrence at <see cref="NextRunAt"/> produced <paramref name="jobId"/> (or was
    /// skipped when null), and moves on to <paramref name="nextRunAt"/>.
    /// </summary>
    public void RecordFired(Guid? jobId, DateTimeOffset? nextRunAt, DateTimeOffset now)
    {
        if (!IsDue(now))
            throw new DomainException($"Schedule {Id} is not due.");
        if (nextRunAt <= NextRunAt)
            throw new DomainException("The next run must be later than the one that just fired.");

        LastRunAt = NextRunAt;
        if (jobId is not null)
            LastJobId = jobId;
        NextRunAt = nextRunAt?.ToUniversalTime();
        Touch(now);
    }

    public void Pause(DateTimeOffset now)
    {
        if (!Enabled)
            throw new DomainException($"Schedule '{Name}' is already paused.");

        Enabled = false;
        Touch(now);
    }

    /// <param name="nextRunAt">
    /// Computed from the resume time, so occurrences missed while paused are skipped, not replayed.
    /// </param>
    public void Resume(DateTimeOffset? nextRunAt, DateTimeOffset now)
    {
        if (Enabled)
            throw new DomainException($"Schedule '{Name}' is not paused.");

        Enabled = true;
        NextRunAt = nextRunAt?.ToUniversalTime();
        Touch(now);
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = now.ToUniversalTime();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _.-]*$")]
    private static partial Regex NamePattern();
}

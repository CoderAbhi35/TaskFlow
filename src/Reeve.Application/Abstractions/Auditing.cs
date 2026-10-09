namespace Reeve.Application.Abstractions;

/// <summary>Who is making the current request, and which request it is.</summary>
public interface IRequestContext
{
    /// <summary>The authenticated subject (user or client), or <see cref="System"/> for background work.</summary>
    string Actor { get; }

    string? CorrelationId { get; }

    public const string System = "system";
}

/// <summary>
/// Records audit events as part of the current unit of work: they are committed with the change
/// they describe, or not at all.
/// </summary>
public interface IAuditLog
{
    /// <param name="actor">Overrides the request's actor, e.g. the user a token was just issued to.</param>
    void Record(string action, string entityType, string entityId, object? details = null, string? actor = null);
}

public static class AuditActions
{
    public const string JobCreated = "job.created";
    public const string JobCancelled = "job.cancelled";
    public const string JobRetried = "job.retried";
    public const string ScheduleCreated = "schedule.created";
    public const string SchedulePaused = "schedule.paused";
    public const string ScheduleResumed = "schedule.resumed";
    public const string ScheduleDeleted = "schedule.deleted";
    public const string TokenIssued = "auth.token_issued";
    public const string LoginFailed = "auth.login_failed";
}

public static class AuditEntities
{
    public const string Job = "job";
    public const string Schedule = "schedule";
    public const string User = "user";
}

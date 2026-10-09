using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Reeve.Application.Abstractions;
using Reeve.Application.Telemetry;
using Reeve.Application.Schedules;
using Reeve.Domain.Jobs;

namespace Reeve.Infrastructure.Persistence;

/// <summary>
/// Fires due schedules. Creating the job and advancing the schedule happen in the same transaction,
/// which is what makes each occurrence produce exactly one job: a crash rolls back both, a commit
/// makes both visible, and SKIP LOCKED keeps concurrent schedulers off the same schedule. This is one
/// of the few places the system has exactly-once effects, because both writes go to one database.
/// </summary>
internal sealed partial class ScheduleRunner(ReeveDbContext db, TimeProvider time, ILogger<ScheduleRunner> logger)
    : IScheduleRunner
{
    public async Task<int> FireDueSchedulesAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value"
            FROM schedules
            WHERE enabled AND next_run_at <= {now}
            ORDER BY next_run_at
            LIMIT {batchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (ids.Count == 0)
            return 0;

        var schedules = await db.Schedules.Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        var types = schedules.Select(s => s.JobType).Distinct().ToList();
        var enabledTypes = await db.JobTypes.AsNoTracking()
            .Where(t => types.Contains(t.Type) && t.Enabled)
            .Select(t => t.Type)
            .ToListAsync(cancellationToken);

        foreach (var schedule in schedules)
        {
            var occurrence = schedule.NextRunAt!.Value;
            Guid? jobId = null;

            // Each run starts its own trace; the job carries it through dispatch and execution.
            using var activity = ReeveTelemetry.Source.StartActivity($"schedule {schedule.Name}", ActivityKind.Internal, parentContext: default);
            activity?.SetTag("reeve.schedule.id", schedule.Id);

            if (enabledTypes.Contains(schedule.JobType))
            {
                // The key names the occurrence, so any job can be traced back to the run that made it,
                // and a second job for the same occurrence is impossible even outside this transaction.
                var job = Job.Create(schedule.JobType, schedule.Payload, schedule.Priority, schedule.RetryPolicy, now,
                    idempotencyKey: $"schedule:{schedule.Id:N}:{occurrence.UtcDateTime:yyyyMMddTHHmmssZ}",
                    traceParent: ReeveTelemetry.CurrentTraceParent(),
                    idempotencyScope: IRequestContext.System);
                db.Jobs.Add(job);
                jobId = job.Id;
                ReeveTelemetry.JobsSubmitted.Add(1,
                    new(ReeveTelemetry.JobTypeTag, job.Type), new(ReeveTelemetry.SourceTag, "schedule"));
            }
            else
            {
                LogSkippedDisabledType(schedule.Name, schedule.JobType);
            }

            // Computed from now, not from the occurrence that fired: after downtime the schedule runs once
            // to catch up and then continues, instead of replaying every missed occurrence at once.
            var next = CronSchedule.NextAfter(schedule.CronExpression, schedule.TimeZone, now);
            schedule.RecordFired(jobId, next, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return schedules.Count;
    }

    [LoggerMessage(LogLevel.Warning, "Schedule '{Name}' skipped a run: job type {JobType} is disabled or unregistered")]
    private partial void LogSkippedDisabledType(string name, string jobType);
}

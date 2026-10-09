using Microsoft.EntityFrameworkCore;
using Reeve.Application.Execution;
using Reeve.Domain.Jobs;

namespace Reeve.Infrastructure.Persistence;

internal sealed class JobEffectLedger(ReeveDbContext db, TimeProvider time) : IJobEffectLedger
{
    public Task<bool> IsRecordedAsync(Guid jobId, string key, CancellationToken cancellationToken = default) =>
        db.JobEffects.AnyAsync(e => e.JobId == jobId && e.Key == key, cancellationToken);

    public Task<bool> IsAttemptCurrentAsync(Guid jobId, int attemptNumber, CancellationToken cancellationToken = default) =>
        // An attempt stays Running only while it is the job's current one: cancelling, recovery and
        // recording an outcome all close it.
        db.JobAttempts.AnyAsync(
            a => a.JobId == jobId && a.AttemptNumber == attemptNumber && a.Status == JobAttemptStatus.Running,
            cancellationToken);

    public async Task RecordAsync(Guid jobId, string key, int attemptNumber, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        // ON CONFLICT DO NOTHING: two attempts racing to record the same effect is not an error.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO job_effects (job_id, key, attempt_number, recorded_at)
            VALUES ({jobId}, {key}, {attemptNumber}, {now})
            ON CONFLICT (job_id, key) DO NOTHING
            """, cancellationToken);
    }
}

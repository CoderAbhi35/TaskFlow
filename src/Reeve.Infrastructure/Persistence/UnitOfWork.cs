using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reeve.Application.Abstractions;
using Reeve.Domain.Jobs;
using Reeve.Domain.Schedules;

namespace Reeve.Infrastructure.Persistence;

internal sealed class UnitOfWork(ReeveDbContext db) : IUnitOfWork
{
    internal const string IdempotencyKeyIndex = "ix_jobs_idempotency_key";
    internal const string ScheduleNameIndex = "ix_schedules_name";

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException(
                "The record was modified by another process after it was loaded.", ex);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, IdempotencyKeyIndex))
        {
            var key = ex.Entries.Select(e => e.Entity).OfType<Job>().FirstOrDefault()?.IdempotencyKey ?? "";
            Discard();
            throw new DuplicateIdempotencyKeyException(key, ex);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, ScheduleNameIndex))
        {
            var name = ex.Entries.Select(e => e.Entity).OfType<Schedule>().FirstOrDefault()?.Name ?? "";
            Discard();
            throw new DuplicateScheduleNameException(name, ex);
        }
    }

    /// <summary>
    /// The failed save rolled back every pending change in it, including audit events recorded for
    /// it. Forget them all, so the caller can keep using this context without a later save
    /// resurrecting half of the failed one.
    /// </summary>
    private void Discard() => db.ChangeTracker.Clear();

    private static bool IsUniqueViolation(DbUpdateException ex, string constraint) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == constraint;
}

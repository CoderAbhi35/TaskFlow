using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reeve.Domain.Jobs;

namespace Reeve.Infrastructure.Persistence.Configurations;

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");

        // IDs are generated in the domain (UUIDv7), so EF must not treat a set key as "already persisted".
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();

        builder.Property(j => j.Type).HasMaxLength(Job.MaxTypeLength).IsRequired();
        builder.Property(j => j.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(j => j.Priority); // int, so ORDER BY priority DESC works
        builder.Property(j => j.IdempotencyKey).HasMaxLength(Job.MaxIdempotencyKeyLength);
        builder.Property(j => j.IdempotencyScope).HasMaxLength(Job.MaxIdempotencyScopeLength);
        builder.Property(j => j.IdempotencyFingerprint).HasMaxLength(Job.MaxIdempotencyFingerprintLength);
        builder.Property(j => j.TraceParent).HasMaxLength(Job.MaxTraceParentLength);
        builder.Property(j => j.LastError).HasMaxLength(Job.MaxErrorLength);

        builder.Ignore(j => j.RetryPolicy);
        builder.Ignore(j => j.CurrentAttempt);

        // Optimistic concurrency on PostgreSQL's system row version. A stale writer (e.g. a worker
        // completing a job that was cancelled meanwhile) gets a conflict instead of a lost update.
        // Npgsql maps a uint row version to the xmin system column, so no real column is created.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasMany(j => j.Attempts)
            .WithOne()
            .HasForeignKey(a => a.JobId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(j => j.Attempts).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Queue selection: WHERE status = 'Pending' ORDER BY priority DESC, created_at.
        builder.HasIndex(j => new { j.Status, j.Priority, j.CreatedAt })
            .IsDescending(false, true, false);
        builder.HasIndex(j => j.ScheduledAt);
        // Dashboard windows ("submitted / finished in the last hour") and createdAfter filters.
        builder.HasIndex(j => j.CreatedAt);
        builder.HasIndex(j => j.CompletedAt).HasFilter("completed_at IS NOT NULL");
        // Unique per caller, not globally (see Job.IdempotencyScope). The name is what UnitOfWork
        // recognises when a concurrent request with the same key loses the race.
        builder.HasIndex(j => new { j.IdempotencyScope, j.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName(UnitOfWork.IdempotencyKeyIndex);
    }
}

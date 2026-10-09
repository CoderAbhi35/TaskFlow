using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reeve.Domain.Jobs;
using Reeve.Domain.Workers;

namespace Reeve.Infrastructure.Persistence.Configurations;

internal sealed class JobAttemptConfiguration : IEntityTypeConfiguration<JobAttempt>
{
    public void Configure(EntityTypeBuilder<JobAttempt> builder)
    {
        builder.ToTable("job_attempts");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.WorkerId).HasMaxLength(WorkerNode.MaxIdLength).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.Error).HasMaxLength(Job.MaxErrorLength);
        builder.Ignore(a => a.Duration);

        // Also enforces that two workers cannot record the same attempt number for a job.
        builder.HasIndex(a => new { a.JobId, a.AttemptNumber }).IsUnique();
    }
}

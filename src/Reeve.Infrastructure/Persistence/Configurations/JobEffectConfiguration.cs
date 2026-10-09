using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reeve.Domain.Jobs;

namespace Reeve.Infrastructure.Persistence.Configurations;

internal sealed class JobEffectConfiguration : IEntityTypeConfiguration<JobEffect>
{
    public void Configure(EntityTypeBuilder<JobEffect> builder)
    {
        builder.ToTable("job_effects");

        // The key is the guarantee: one row per job and effect.
        builder.HasKey(e => new { e.JobId, e.Key });
        builder.Property(e => e.Key).HasMaxLength(JobEffect.MaxKeyLength);

        builder.HasOne<Job>().WithMany().HasForeignKey(e => e.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}

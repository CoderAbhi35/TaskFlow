using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reeve.Domain.Workers;

namespace Reeve.Infrastructure.Persistence.Configurations;

internal sealed class WorkerNodeConfiguration : IEntityTypeConfiguration<WorkerNode>
{
    public void Configure(EntityTypeBuilder<WorkerNode> builder)
    {
        builder.ToTable("workers");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).HasMaxLength(WorkerNode.MaxIdLength).ValueGeneratedNever();
        builder.Property(w => w.Hostname).HasMaxLength(255).IsRequired();
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(w => w.SupportedJobTypes); // text[]

        // Stale-worker detection scans by heartbeat age.
        builder.HasIndex(w => w.LastHeartbeatAt);
    }
}

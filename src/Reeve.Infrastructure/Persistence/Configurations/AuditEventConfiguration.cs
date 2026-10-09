using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reeve.Domain.Audit;

namespace Reeve.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Actor).HasMaxLength(AuditEvent.MaxActorLength).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(AuditEvent.MaxActionLength).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(AuditEvent.MaxEntityTypeLength).IsRequired();
        builder.Property(e => e.EntityId).HasMaxLength(AuditEvent.MaxEntityIdLength).IsRequired();
        builder.Property(e => e.CorrelationId).HasMaxLength(64);
        builder.Property(e => e.Details).HasColumnType("jsonb");

        // "What happened to this job?" and "what did this person do?"; ids are time-ordered for paging.
        builder.HasIndex(e => new { e.EntityId, e.Id });
        builder.HasIndex(e => new { e.Actor, e.Id });
    }
}

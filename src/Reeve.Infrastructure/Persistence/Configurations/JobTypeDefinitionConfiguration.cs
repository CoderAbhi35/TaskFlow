using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reeve.Domain.Jobs;

namespace Reeve.Infrastructure.Persistence.Configurations;

internal sealed class JobTypeDefinitionConfiguration : IEntityTypeConfiguration<JobTypeDefinition>
{
    public void Configure(EntityTypeBuilder<JobTypeDefinition> builder)
    {
        builder.ToTable("job_types");

        builder.HasKey(t => t.Type);
        builder.Property(t => t.Type).HasMaxLength(Job.MaxTypeLength).ValueGeneratedNever();
        builder.Ignore(t => t.Timeout);

        // Generic example job types so the API is usable out of the box. Handlers arrive with the worker.
        builder.HasData(
            new { Type = "GENERATE_REPORT", Enabled = true, TimeoutSeconds = 300, MaxRetries = 3 },
            new { Type = "SEND_NOTIFICATION", Enabled = true, TimeoutSeconds = 30, MaxRetries = 5 },
            new { Type = "PROCESS_IMAGE", Enabled = true, TimeoutSeconds = 120, MaxRetries = 3 });
    }
}

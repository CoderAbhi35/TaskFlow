using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reeve.Domain.Jobs;
using Reeve.Domain.Schedules;

namespace Reeve.Infrastructure.Persistence.Configurations;

internal sealed class ScheduleConfiguration : IEntityTypeConfiguration<Schedule>
{
    public void Configure(EntityTypeBuilder<Schedule> builder)
    {
        builder.ToTable("schedules");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Name).HasMaxLength(Schedule.MaxNameLength).IsRequired();
        builder.Property(s => s.JobType).HasMaxLength(Job.MaxTypeLength).IsRequired();
        builder.Property(s => s.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(s => s.CronExpression).HasMaxLength(Schedule.MaxCronLength).IsRequired();
        builder.Property(s => s.TimeZone).HasMaxLength(Schedule.MaxTimeZoneLength).IsRequired();
        builder.Ignore(s => s.RetryPolicy);

        // Pausing, resuming and firing can race; the loser must reload instead of overwriting.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasIndex(s => s.Name).IsUnique();
        // The scheduler's scan: WHERE enabled AND next_run_at <= now ORDER BY next_run_at.
        builder.HasIndex(s => s.NextRunAt).HasFilter("enabled");
    }
}

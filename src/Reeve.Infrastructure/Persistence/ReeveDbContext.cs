using Microsoft.EntityFrameworkCore;
using Reeve.Domain.Audit;
using Reeve.Domain.Jobs;
using Reeve.Domain.Schedules;
using Reeve.Domain.Workers;

namespace Reeve.Infrastructure.Persistence;

public sealed class ReeveDbContext(DbContextOptions<ReeveDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<JobTypeDefinition> JobTypes => Set<JobTypeDefinition>();
    public DbSet<WorkerNode> Workers => Set<WorkerNode>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<JobEffect> JobEffects => Set<JobEffect>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReeveDbContext).Assembly);
}

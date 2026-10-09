using Microsoft.EntityFrameworkCore;
using Reeve.Application.Abstractions;
using Reeve.Domain.Jobs;

namespace Reeve.Infrastructure.Persistence;

internal sealed class JobRepository(ReeveDbContext db) : IJobRepository
{
    public void Add(Job job) => db.Jobs.Add(job);

    public Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Jobs
            .Include(j => j.Attempts.OrderBy(a => a.AttemptNumber))
            .SingleOrDefaultAsync(j => j.Id == id, cancellationToken);

    public Task<Job?> GetByIdempotencyKeyAsync(string scope, string idempotencyKey, CancellationToken cancellationToken = default) =>
        db.Jobs
            .Include(j => j.Attempts.OrderBy(a => a.AttemptNumber))
            .SingleOrDefaultAsync(j => j.IdempotencyScope == scope && j.IdempotencyKey == idempotencyKey, cancellationToken);
}

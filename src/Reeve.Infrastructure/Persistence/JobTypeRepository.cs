using Microsoft.EntityFrameworkCore;
using Reeve.Application.Abstractions;
using Reeve.Domain.Jobs;

namespace Reeve.Infrastructure.Persistence;

internal sealed class JobTypeRepository(ReeveDbContext db) : IJobTypeRepository
{
    public Task<JobTypeDefinition?> GetAsync(string type, CancellationToken cancellationToken = default) =>
        db.JobTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Type == type, cancellationToken);
}

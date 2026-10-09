using Reeve.Domain.Jobs;

namespace Reeve.Application.Abstractions;

public interface IJobTypeRepository
{
    Task<JobTypeDefinition?> GetAsync(string type, CancellationToken cancellationToken = default);
}

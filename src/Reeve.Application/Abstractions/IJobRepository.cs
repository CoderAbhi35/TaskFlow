using Reeve.Domain.Jobs;

namespace Reeve.Application.Abstractions;

public interface IJobRepository
{
    void Add(Job job);

    /// <summary>Loads a job with its attempt history, tracked for modification.</summary>
    Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <param name="scope">Whose key it is (see <see cref="Job.IdempotencyScope"/>).</param>
    Task<Job?> GetByIdempotencyKeyAsync(string scope, string idempotencyKey, CancellationToken cancellationToken = default);
}

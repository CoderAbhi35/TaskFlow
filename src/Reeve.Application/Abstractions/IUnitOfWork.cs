namespace Reeve.Application.Abstractions;

public interface IUnitOfWork
{
    /// <summary>Persists all pending changes atomically.</summary>
    /// <exception cref="ConcurrencyConflictException">Another writer changed the same row first.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

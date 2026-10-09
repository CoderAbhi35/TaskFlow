namespace Reeve.Application.Abstractions;

/// <summary>
/// Moves ready jobs from PostgreSQL to the transport. The jobs table acts as the outbox: a job is
/// only marked Queued after the transport has acknowledged its message, so nothing is lost if either
/// side fails; the price is that a crash between publish and commit can publish a job twice.
/// </summary>
public interface IJobDispatcher
{
    /// <returns>The number of jobs published and marked Queued.</returns>
    Task<int> DispatchReadyJobsAsync(int batchSize, CancellationToken cancellationToken = default);
}

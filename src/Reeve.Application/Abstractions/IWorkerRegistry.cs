using Reeve.Domain.Workers;

namespace Reeve.Application.Abstractions;

public interface IWorkerRegistry
{
    Task RegisterAsync(WorkerNode worker, CancellationToken cancellationToken = default);

    /// <returns>False if the worker is no longer registered.</returns>
    Task<bool> HeartbeatAsync(string workerId, CancellationToken cancellationToken = default);

    Task SetStatusAsync(string workerId, WorkerStatus status, CancellationToken cancellationToken = default);
}

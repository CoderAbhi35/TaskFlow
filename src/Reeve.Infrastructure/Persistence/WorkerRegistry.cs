using Microsoft.EntityFrameworkCore;
using Reeve.Application.Abstractions;
using Reeve.Domain.Workers;

namespace Reeve.Infrastructure.Persistence;

internal sealed class WorkerRegistry(ReeveDbContext db, TimeProvider time) : IWorkerRegistry
{
    public async Task RegisterAsync(WorkerNode worker, CancellationToken cancellationToken = default)
    {
        db.Workers.Add(worker);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> HeartbeatAsync(string workerId, CancellationToken cancellationToken = default)
    {
        var worker = await db.Workers.SingleOrDefaultAsync(w => w.Id == workerId, cancellationToken);
        if (worker is null)
            return false;

        worker.Heartbeat(time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SetStatusAsync(string workerId, WorkerStatus status, CancellationToken cancellationToken = default)
    {
        var worker = await db.Workers.SingleOrDefaultAsync(w => w.Id == workerId, cancellationToken);
        if (worker is null)
            return;

        switch (status)
        {
            case WorkerStatus.Draining: worker.BeginDraining(); break;
            case WorkerStatus.Offline: worker.MarkOffline(); break;
            case WorkerStatus.Active: worker.Heartbeat(time.GetUtcNow()); break;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

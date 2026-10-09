using Reeve.Application.Abstractions;
using Reeve.Application.Common;
using Reeve.Contracts.Jobs;

namespace Reeve.Application.Jobs;

/// <summary>Operator actions on an existing job. Invalid transitions surface as domain exceptions.</summary>
public sealed class JobLifecycleHandler(IJobRepository jobs, IUnitOfWork unitOfWork, IAuditLog audit, TimeProvider time)
{
    public async Task<JobResponse> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException("Job", id);
        job.Cancel(time.GetUtcNow());
        audit.Record(AuditActions.JobCancelled, AuditEntities.Job, job.Id.ToString(), new { job.Type });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return job.ToResponse();
    }

    public async Task<JobResponse> RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException("Job", id);
        job.Requeue(time.GetUtcNow());
        audit.Record(AuditActions.JobRetried, AuditEntities.Job, job.Id.ToString(), new { job.Type, job.AttemptCount });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return job.ToResponse();
    }
}

using Reeve.Contracts.Messages;

namespace Reeve.Application.Abstractions;

/// <param name="TraceParent">The job's stored trace; publishing continues it and passes it on to the consumer.</param>
public sealed record OutgoingJob(JobDispatchMessage Message, string? TraceParent);

public interface IJobPublisher
{
    /// <summary>
    /// Publishes the messages and waits for the broker to acknowledge them durably.
    /// </summary>
    /// <returns>The IDs of the jobs whose messages were acknowledged. Others were not published.</returns>
    Task<IReadOnlySet<Guid>> PublishAsync(IReadOnlyList<OutgoingJob> jobs, CancellationToken cancellationToken = default);
}

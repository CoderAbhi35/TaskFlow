using Reeve.Domain.Schedules;

namespace Reeve.Application.Abstractions;

public interface IScheduleRepository
{
    void Add(Schedule schedule);

    void Remove(Schedule schedule);

    Task<Schedule?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Turns due schedule occurrences into jobs.</summary>
public interface IScheduleRunner
{
    /// <summary>
    /// For each due schedule: creates the job for its current occurrence and advances it to the next,
    /// in one transaction, so every occurrence produces exactly one job even if the scheduler crashes
    /// or several schedulers run.
    /// </summary>
    /// <returns>The number of schedules fired.</returns>
    Task<int> FireDueSchedulesAsync(int batchSize, CancellationToken cancellationToken = default);
}

/// <summary>Safety net for jobs whose dispatch message was never consumed.</summary>
public interface IQueuedJobSweeper
{
    /// <returns>The number of jobs returned to Pending for re-dispatch.</returns>
    Task<int> RequeueStaleAsync(TimeSpan queuedFor, CancellationToken cancellationToken = default);
}

/// <summary>Another schedule already uses this name.</summary>
public sealed class DuplicateScheduleNameException(string name, Exception innerException)
    : Exception($"A schedule named '{name}' already exists.", innerException)
{
    public string Name { get; } = name;
}

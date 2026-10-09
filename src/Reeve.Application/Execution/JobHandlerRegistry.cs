namespace Reeve.Application.Execution;

public sealed class JobHandlerRegistry
{
    private readonly Dictionary<string, IJobHandler> _handlers;

    public JobHandlerRegistry(IEnumerable<IJobHandler> handlers)
    {
        _handlers = new Dictionary<string, IJobHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.JobType, handler))
                throw new InvalidOperationException($"More than one handler is registered for job type '{handler.JobType}'.");
        }
    }

    public IReadOnlyCollection<string> JobTypes => _handlers.Keys;

    public bool TryGet(string jobType, out IJobHandler handler) => _handlers.TryGetValue(jobType, out handler!);
}

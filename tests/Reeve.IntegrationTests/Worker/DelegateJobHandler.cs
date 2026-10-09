using Reeve.Application.Execution;

namespace Reeve.IntegrationTests.Worker;

/// <summary>A job handler whose behaviour is supplied by the test.</summary>
internal sealed class DelegateJobHandler(string jobType, Func<JobExecutionContext, CancellationToken, Task> execute)
    : IJobHandler
{
    public string JobType => jobType;

    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken) =>
        execute(context, cancellationToken);

    public static DelegateJobHandler Succeeds(string jobType) => new(jobType, (_, _) => Task.CompletedTask);
}

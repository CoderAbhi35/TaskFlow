using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reeve.Application.Abstractions;
using Reeve.Application.Execution;
using Reeve.Domain.Jobs;

namespace Reeve.UnitTests.Application;

/// <summary>
/// Recording an outcome can fail (the database restarting). The executor retries, so the job
/// doesn't sit in Running until recovery notices it.
/// </summary>
public class JobExecutorRecordingTests
{
    private const string Type = "UNIT_TEST";

    private sealed class Succeeds : IJobHandler
    {
        public string JobType => Type;
        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>What a fresh load from the database returns: the job, running its first attempt.</summary>
    private static Job RunningJob()
    {
        var job = Job.Create(Type, "{}", JobPriority.Normal, RetryPolicy.Default, DateTimeOffset.UtcNow);
        job.Start("worker-1", DateTimeOffset.UtcNow);
        return job;
    }

    private static (JobExecutor Executor, List<Job> Loaded) Build(Mock<IUnitOfWork> unitOfWork)
    {
        var loaded = new List<Job>();
        var repository = new Mock<IJobRepository>();
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { var job = RunningJob(); loaded.Add(job); return job; });

        var services = new ServiceCollection()
            .AddSingleton(repository.Object)
            .AddSingleton(unitOfWork.Object)
            .BuildServiceProvider();
        var executor = new JobExecutor(new JobHandlerRegistry([new Succeeds()]),
            services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<JobExecutor>.Instance);
        return (executor, loaded);
    }

    private static ClaimedJob Claimed() => new(Guid.NewGuid(), Type, "{}", AttemptNumber: 1, TimeSpan.FromSeconds(30));

    [Fact]
    public async Task A_failed_save_is_retried_with_a_fresh_load()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupSequence(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"))
            .Returns(Task.CompletedTask);
        var (executor, loaded) = Build(unitOfWork);

        await executor.ExecuteAsync(Claimed(), CancellationToken.None);

        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        loaded.Should().HaveCount(2, "each try reloads, so a half-applied change from the failed try is not reused");
        loaded[^1].Status.Should().Be(JobStatus.Succeeded);
    }

    [Fact]
    public async Task A_rejected_result_is_discarded_without_retrying()
    {
        // The attempt was cancelled or replaced: the domain refuses the result, and retrying can't help.
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IJobRepository>();
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            var job = RunningJob();
            job.Cancel(DateTimeOffset.UtcNow);
            return job;
        });
        var services = new ServiceCollection().AddSingleton(repository.Object).AddSingleton(unitOfWork.Object).BuildServiceProvider();
        var executor = new JobExecutor(new JobHandlerRegistry([new Succeeds()]),
            services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<JobExecutor>.Instance);

        await executor.ExecuteAsync(Claimed(), CancellationToken.None);

        repository.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}

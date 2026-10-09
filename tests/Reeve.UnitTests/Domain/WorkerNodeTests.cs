using FluentAssertions;
using Reeve.Domain;
using Reeve.Domain.Workers;

namespace Reeve.UnitTests.Domain;

public class WorkerNodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static WorkerNode NewWorker() =>
        WorkerNode.Register("host-a-1", "host-a", concurrency: 4, ["GENERATE_REPORT", "SEND_EMAIL"], Now);

    [Fact]
    public void Register_creates_active_worker()
    {
        var worker = NewWorker();

        worker.Status.Should().Be(WorkerStatus.Active);
        worker.Concurrency.Should().Be(4);
        worker.SupportedJobTypes.Should().Equal("GENERATE_REPORT", "SEND_EMAIL");
        worker.LastHeartbeatAt.Should().Be(Now);
    }

    [Fact]
    public void Register_removes_duplicate_and_blank_job_types()
    {
        var worker = WorkerNode.Register("w", "h", 1, ["A", "A", " ", "B"], Now);

        worker.SupportedJobTypes.Should().Equal("A", "B");
    }

    [Fact]
    public void Register_validates_input()
    {
        FluentActions.Invoking(() => WorkerNode.Register("", "h", 1, ["A"], Now)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => WorkerNode.Register("w", "", 1, ["A"], Now)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => WorkerNode.Register("w", "h", 0, ["A"], Now)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => WorkerNode.Register("w", "h", 1, [], Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Worker_becomes_stale_after_missing_heartbeats()
    {
        var worker = NewWorker();
        var timeout = TimeSpan.FromSeconds(30);

        worker.IsStale(Now.AddSeconds(30), timeout).Should().BeFalse();
        worker.IsStale(Now.AddSeconds(31), timeout).Should().BeTrue();

        worker.Heartbeat(Now.AddSeconds(31));
        worker.IsStale(Now.AddSeconds(31), timeout).Should().BeFalse();
    }

    [Fact]
    public void Heartbeat_revives_offline_worker()
    {
        var worker = NewWorker();
        worker.MarkOffline();

        worker.Heartbeat(Now.AddMinutes(1));

        worker.Status.Should().Be(WorkerStatus.Active);
    }

    [Fact]
    public void Draining_worker_accepts_no_new_work()
    {
        var worker = NewWorker();
        worker.CanAccept("GENERATE_REPORT").Should().BeTrue();
        worker.CanAccept("UNKNOWN").Should().BeFalse();

        worker.BeginDraining();

        worker.CanAccept("GENERATE_REPORT").Should().BeFalse();
    }
}

using FluentAssertions;
using Reeve.Domain;
using Reeve.Domain.Jobs;

namespace Reeve.UnitTests.Domain;

public class JobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Random Jitter = new(42);

    private static Job NewJob(RetryPolicy? retryPolicy = null, DateTimeOffset? scheduledAt = null) =>
        Job.Create("GENERATE_REPORT", """{"customerId":12345}""", JobPriority.High,
            retryPolicy ?? RetryPolicy.Default, Now, scheduledAt);

    private static Job RunningJob(RetryPolicy? retryPolicy = null)
    {
        var job = NewJob(retryPolicy);
        job.Start("worker-1", Now);
        return job;
    }

    [Fact]
    public void Create_sets_initial_state()
    {
        var job = NewJob();

        job.Id.Should().NotBeEmpty();
        job.Id.Version.Should().Be(7);
        job.Status.Should().Be(JobStatus.Pending);
        job.Type.Should().Be("GENERATE_REPORT");
        job.Priority.Should().Be(JobPriority.High);
        job.MaxRetries.Should().Be(3);
        job.CreatedAt.Should().Be(Now);
        job.UpdatedAt.Should().Be(Now);
        job.AttemptCount.Should().Be(0);
        job.Attempts.Should().BeEmpty();
    }

    [Fact]
    public void Create_normalises_times_to_utc()
    {
        var local = new DateTimeOffset(2026, 9, 23, 17, 30, 0, TimeSpan.FromHours(5.5));

        var job = Job.Create("X", null, JobPriority.Normal, RetryPolicy.Default, local, scheduledAt: local);

        job.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
        job.ScheduledAt!.Value.Offset.Should().Be(TimeSpan.Zero);
        job.CreatedAt.Should().Be(Now);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_defaults_missing_payload_to_empty_object(string? payload)
    {
        var job = Job.Create("X", payload, JobPriority.Normal, RetryPolicy.Default, Now);

        job.Payload.Should().Be("{}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("has space")]
    [InlineData("_leading-underscore")]
    [InlineData("semi;colon")]
    public void Create_rejects_invalid_type(string type)
    {
        var act = () => Job.Create(type, null, JobPriority.Normal, RetryPolicy.Default, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_rejects_type_longer_than_limit()
    {
        var act = () => Job.Create(new string('A', Job.MaxTypeLength + 1), null, JobPriority.Normal,
            RetryPolicy.Default, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_rejects_undefined_priority()
    {
        var act = () => Job.Create("X", null, (JobPriority)99, RetryPolicy.Default, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_trims_idempotency_key_and_treats_blank_as_none()
    {
        Job.Create("X", null, JobPriority.Normal, RetryPolicy.Default, Now, idempotencyKey: "  key-1 ")
            .IdempotencyKey.Should().Be("key-1");
        Job.Create("X", null, JobPriority.Normal, RetryPolicy.Default, Now, idempotencyKey: "  ")
            .IdempotencyKey.Should().BeNull();
    }

    [Fact]
    public void Create_requires_a_key_for_an_idempotency_fingerprint()
    {
        var act = () => Job.Create("X", null, JobPriority.Normal, RetryPolicy.Default, Now,
            idempotencyFingerprint: new string('a', 64));

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void IsReadyToRun_respects_schedule()
    {
        var job = NewJob(scheduledAt: Now.AddMinutes(10));

        job.IsReadyToRun(Now).Should().BeFalse();
        job.IsReadyToRun(Now.AddMinutes(10)).Should().BeTrue();
    }

    [Fact]
    public void MarkQueued_moves_pending_to_queued()
    {
        var job = NewJob();

        job.MarkQueued(Now.AddSeconds(1));

        job.Status.Should().Be(JobStatus.Queued);
        job.UpdatedAt.Should().Be(Now.AddSeconds(1));
    }

    [Fact]
    public void MarkQueued_rejects_job_scheduled_in_the_future()
    {
        var job = NewJob(scheduledAt: Now.AddMinutes(5));

        var act = () => job.MarkQueued(Now);

        act.Should().Throw<DomainException>();
        job.Status.Should().Be(JobStatus.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Start_opens_attempt_from_pending_or_queued(bool queueFirst)
    {
        var job = NewJob();
        if (queueFirst)
            job.MarkQueued(Now);

        var attempt = job.Start("worker-1", Now.AddSeconds(2));

        job.Status.Should().Be(JobStatus.Running);
        job.AttemptCount.Should().Be(1);
        job.CurrentAttempt.Should().BeSameAs(attempt);
        attempt.AttemptNumber.Should().Be(1);
        attempt.JobId.Should().Be(job.Id);
        attempt.WorkerId.Should().Be("worker-1");
        attempt.Status.Should().Be(JobAttemptStatus.Running);
        attempt.StartedAt.Should().Be(Now.AddSeconds(2));
    }

    [Fact]
    public void Start_rejects_second_claim_while_running()
    {
        var job = RunningJob();

        var act = () => job.Start("worker-2", Now);

        act.Should().Throw<InvalidJobStateTransitionException>()
            .Which.From.Should().Be(JobStatus.Running);
        job.Attempts.Should().ContainSingle();
    }

    [Fact]
    public void Start_requires_worker_id()
    {
        var act = () => NewJob().Start(" ", Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Succeed_completes_job_and_attempt()
    {
        var job = RunningJob();

        job.Succeed(Now.AddSeconds(30));

        job.Status.Should().Be(JobStatus.Succeeded);
        job.CompletedAt.Should().Be(Now.AddSeconds(30));
        job.CurrentAttempt.Should().BeNull();
        var attempt = job.Attempts.Single();
        attempt.Status.Should().Be(JobAttemptStatus.Succeeded);
        attempt.Duration.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Queued_job_can_be_returned_to_pending_for_redispatch()
    {
        var job = NewJob();
        job.MarkQueued(Now);

        job.ReturnToPending(Now.AddMinutes(10));

        job.Status.Should().Be(JobStatus.Pending);
        job.UpdatedAt.Should().Be(Now.AddMinutes(10));
        job.Invoking(j => j.ReturnToPending(Now)).Should().Throw<InvalidJobStateTransitionException>();
    }

    [Fact]
    public void Report_for_the_current_attempt_is_accepted()
    {
        var job = RunningJob();

        job.Succeed(Now, attemptNumber: 1);

        job.Status.Should().Be(JobStatus.Succeeded);
    }

    [Fact]
    public void Late_report_for_an_earlier_attempt_is_rejected()
    {
        var job = RunningJob(new RetryPolicy(maxRetries: 3, backoffSeconds: 1));
        job.Fail("worker lost", isTransient: true, Now, Jitter, attemptNumber: 1);
        job.Start("worker-2", job.ScheduledAt!.Value);

        var succeed = () => job.Succeed(Now, attemptNumber: 1);
        var fail = () => job.Fail("late", isTransient: false, Now, Jitter, attemptNumber: 1);

        succeed.Should().Throw<StaleAttemptException>().Which.CurrentAttempt.Should().Be(2);
        fail.Should().Throw<StaleAttemptException>();
        job.Status.Should().Be(JobStatus.Running);
        job.CurrentAttempt!.WorkerId.Should().Be("worker-2");
    }

    [Fact]
    public void Duplicate_completion_is_rejected()
    {
        var job = RunningJob();
        job.Succeed(Now);

        var act = () => job.Succeed(Now.AddSeconds(1));

        act.Should().Throw<InvalidJobStateTransitionException>();
    }

    [Fact]
    public void Transient_failure_with_retries_left_schedules_retry()
    {
        var job = RunningJob(new RetryPolicy(maxRetries: 3, backoffSeconds: 10));

        job.Fail("Connection reset", isTransient: true, Now, Jitter);

        job.Status.Should().Be(JobStatus.Pending);
        job.RetryCount.Should().Be(1);
        job.LastError.Should().Be("Connection reset");
        job.CompletedAt.Should().BeNull();
        job.ScheduledAt.Should().BeOnOrAfter(Now.AddSeconds(8)).And.BeOnOrBefore(Now.AddSeconds(12));
        job.Attempts.Single().Status.Should().Be(JobAttemptStatus.Failed);
        job.Attempts.Single().Error.Should().Be("Connection reset");
    }

    [Fact]
    public void Transient_failures_dead_letter_after_retries_are_exhausted()
    {
        var job = NewJob(new RetryPolicy(maxRetries: 2, backoffSeconds: 1));

        for (var i = 0; i < 3; i++)
        {
            var now = job.ScheduledAt ?? Now;
            job.Start("worker-1", now);
            job.Fail("Timeout", isTransient: true, now, Jitter);
        }

        job.Status.Should().Be(JobStatus.DeadLettered);
        job.AttemptCount.Should().Be(3);
        job.RetryCount.Should().Be(2);
        job.CompletedAt.Should().NotBeNull();
        job.Attempts.Select(a => a.AttemptNumber).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Permanent_failure_is_not_retried()
    {
        var job = RunningJob(new RetryPolicy(maxRetries: 5, backoffSeconds: 1));

        job.Fail("Customer not found", isTransient: false, Now, Jitter);

        job.Status.Should().Be(JobStatus.Failed);
        job.RetryCount.Should().Be(0);
        job.CompletedAt.Should().Be(Now);
    }

    [Fact]
    public void Fail_truncates_long_errors()
    {
        var job = RunningJob();

        job.Fail(new string('e', Job.MaxErrorLength + 500), isTransient: false, Now, Jitter);

        job.LastError!.Length.Should().Be(Job.MaxErrorLength);
        job.Attempts.Single().Error!.Length.Should().Be(Job.MaxErrorLength);
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Queued)]
    public void Cancel_stops_job_that_has_not_started(JobStatus status)
    {
        var job = NewJob();
        if (status == JobStatus.Queued)
            job.MarkQueued(Now);

        job.Cancel(Now.AddSeconds(5));

        job.Status.Should().Be(JobStatus.Cancelled);
        job.CompletedAt.Should().Be(Now.AddSeconds(5));
    }

    [Fact]
    public void Cancel_closes_running_attempt_and_rejects_late_completion()
    {
        var job = RunningJob();

        job.Cancel(Now.AddSeconds(5));

        job.Status.Should().Be(JobStatus.Cancelled);
        job.Attempts.Single().Status.Should().Be(JobAttemptStatus.Cancelled);
        job.Invoking(j => j.Succeed(Now.AddSeconds(6)))
            .Should().Throw<InvalidJobStateTransitionException>();
    }

    [Fact]
    public void Cancel_rejects_completed_job()
    {
        var job = RunningJob();
        job.Succeed(Now);

        job.Invoking(j => j.Cancel(Now)).Should().Throw<InvalidJobStateTransitionException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Requeue_restores_failed_or_dead_lettered_job_with_fresh_retry_budget(bool deadLettered)
    {
        var job = RunningJob(new RetryPolicy(maxRetries: 0, backoffSeconds: 1));
        job.Fail("boom", isTransient: deadLettered, Now, Jitter);
        job.Status.Should().Be(deadLettered ? JobStatus.DeadLettered : JobStatus.Failed);

        job.Requeue(Now.AddMinutes(1));

        job.Status.Should().Be(JobStatus.Pending);
        job.RetryCount.Should().Be(0);
        job.ScheduledAt.Should().BeNull();
        job.CompletedAt.Should().BeNull();

        job.Start("worker-2", Now.AddMinutes(2)).AttemptNumber.Should().Be(2);
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Succeeded)]
    [InlineData(JobStatus.Cancelled)]
    public void Requeue_rejects_other_statuses(JobStatus status)
    {
        var job = NewJob();
        if (status != JobStatus.Pending) job.Start("w", Now);
        if (status == JobStatus.Succeeded) job.Succeed(Now);
        if (status == JobStatus.Cancelled) job.Cancel(Now);

        job.Invoking(j => j.Requeue(Now)).Should().Throw<InvalidJobStateTransitionException>();
    }
}

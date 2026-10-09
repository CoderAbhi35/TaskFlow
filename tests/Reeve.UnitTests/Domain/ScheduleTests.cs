using FluentAssertions;
using Reeve.Application.Schedules;
using Reeve.Domain;
using Reeve.Domain.Jobs;
using Reeve.Domain.Schedules;

namespace Reeve.UnitTests.Domain;

public class ScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static Schedule NewSchedule(DateTimeOffset? firstRun = null) =>
        Schedule.Create("Nightly reports", "GENERATE_REPORT", """{"customerId":1}""", JobPriority.Normal,
            RetryPolicy.Default, "0 2 * * *", "UTC", firstRun ?? Now.AddHours(14), Now);

    [Fact]
    public void Create_starts_enabled_with_its_first_run()
    {
        var schedule = NewSchedule();

        schedule.Enabled.Should().BeTrue();
        schedule.NextRunAt.Should().Be(Now.AddHours(14));
        schedule.LastRunAt.Should().BeNull();
        schedule.Id.Version.Should().Be(7);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading-dash")]
    [InlineData("semi;colon")]
    public void Create_rejects_invalid_names(string name)
    {
        var act = () => Schedule.Create(name, "X", null, JobPriority.Normal, RetryPolicy.Default, "* * * * *", "UTC", Now, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Firing_records_the_run_and_moves_to_the_next_occurrence()
    {
        var schedule = NewSchedule(firstRun: Now);
        var jobId = Guid.CreateVersion7();

        schedule.RecordFired(jobId, Now.AddDays(1), Now);

        schedule.LastRunAt.Should().Be(Now);
        schedule.LastJobId.Should().Be(jobId);
        schedule.NextRunAt.Should().Be(Now.AddDays(1));
    }

    [Fact]
    public void Firing_before_the_schedule_is_due_is_rejected()
    {
        var schedule = NewSchedule(firstRun: Now.AddMinutes(1));

        schedule.Invoking(s => s.RecordFired(Guid.NewGuid(), Now.AddDays(1), Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Next_run_must_move_forward()
    {
        var schedule = NewSchedule(firstRun: Now);

        schedule.Invoking(s => s.RecordFired(Guid.NewGuid(), Now, Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Skipped_run_keeps_the_previous_job()
    {
        var schedule = NewSchedule(firstRun: Now);
        var first = Guid.CreateVersion7();
        schedule.RecordFired(first, Now.AddMinutes(1), Now);

        schedule.RecordFired(null, Now.AddMinutes(2), Now.AddMinutes(1));

        schedule.LastJobId.Should().Be(first);
        schedule.LastRunAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Paused_schedule_is_never_due_and_resumes_from_a_fresh_next_run()
    {
        var schedule = NewSchedule(firstRun: Now);
        schedule.Pause(Now);

        schedule.IsDue(Now.AddDays(10)).Should().BeFalse();

        schedule.Resume(Now.AddDays(10).AddHours(2), Now.AddDays(10));
        schedule.Enabled.Should().BeTrue();
        schedule.NextRunAt.Should().Be(Now.AddDays(10).AddHours(2));
    }
}

public class CronScheduleTests
{
    [Fact]
    public void Next_occurrence_is_strictly_after_the_given_time()
    {
        var at = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        CronSchedule.NextAfter("0 * * * *", "UTC", at).Should().Be(at.AddHours(1));
        CronSchedule.NextAfter("*/15 * * * *", "UTC", at.AddMinutes(1)).Should().Be(at.AddMinutes(15));
    }

    [Fact]
    public void Local_time_is_kept_across_daylight_saving_changes()
    {
        // Europe/London moves from GMT to BST on 29 March 2026.
        var beforeSwitch = CronSchedule.NextAfter("0 9 * * *", "Europe/London", new DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero));
        var afterSwitch = CronSchedule.NextAfter("0 9 * * *", "Europe/London", new DateTimeOffset(2026, 3, 30, 0, 0, 0, TimeSpan.Zero));

        beforeSwitch!.Value.UtcDateTime.Hour.Should().Be(9); // 09:00 GMT
        afterSwitch!.Value.UtcDateTime.Hour.Should().Be(8);  // 09:00 BST
    }

    [Theory]
    [InlineData("0 2 * * *", true)]
    [InlineData("@daily", true)]
    [InlineData("*/5 9-17 * * MON-FRI", true)]
    [InlineData("0 0 2 * * *", false)] // 6 fields: seconds are not supported
    [InlineData("61 * * * *", false)]
    [InlineData("not cron", false)]
    [InlineData("", false)]
    public void Validates_expressions(string expression, bool valid)
    {
        CronSchedule.IsValidExpression(expression).Should().Be(valid);
    }

    [Theory]
    [InlineData("UTC", true)]
    [InlineData("Asia/Kolkata", true)]
    [InlineData("America/New_York", true)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("", false)]
    public void Validates_time_zones(string timeZone, bool valid)
    {
        CronSchedule.IsValidTimeZone(timeZone).Should().Be(valid);
    }

    [Fact]
    public void Expression_without_future_occurrences_returns_null()
    {
        // 30 February never happens.
        CronSchedule.NextAfter("0 0 30 2 *", "UTC", DateTimeOffset.UtcNow).Should().BeNull();
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Application.Abstractions;
using Reeve.Domain.Jobs;
using Reeve.Domain.Schedules;
using Reeve.Infrastructure.Persistence;
using Reeve.IntegrationTests.Persistence;

namespace Reeve.IntegrationTests.Scheduling;

[Collection(PostgresCollection.Name)]
public class ScheduleRunnerTests(PostgresFixture fixture)
{
    private async Task<Schedule> AddScheduleAsync(string jobType, string cron, DateTimeOffset nextRunAt, bool paused = false)
    {
        var schedule = Schedule.Create($"test {Guid.NewGuid():N}", jobType, """{"source":"schedule"}""", JobPriority.High,
            new RetryPolicy(2, 10), cron, "UTC", nextRunAt, DateTimeOffset.UtcNow);
        if (paused)
            schedule.Pause(DateTimeOffset.UtcNow);

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReeveDbContext>();
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    private async Task<int> FireAsync()
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IScheduleRunner>().FireDueSchedulesAsync(100);
    }

    private async Task<(Schedule Schedule, List<Job> Jobs)> LoadAsync(Guid scheduleId)
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReeveDbContext>();
        var schedule = await db.Schedules.AsNoTracking().SingleAsync(s => s.Id == scheduleId);
        var prefix = $"schedule:{scheduleId:N}:";
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.IdempotencyKey!.StartsWith(prefix)).ToListAsync();
        return (schedule, jobs);
    }

    [Fact]
    public async Task Due_schedule_creates_a_job_from_its_template_and_advances()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var due = DateTimeOffset.UtcNow.AddSeconds(-5);
        var schedule = await AddScheduleAsync(type, "*/5 * * * *", due);

        await FireAsync();

        var (reloaded, jobs) = await LoadAsync(schedule.Id);
        var job = jobs.Should().ContainSingle().Subject;
        job.Type.Should().Be(type);
        job.Priority.Should().Be(JobPriority.High);
        job.MaxRetries.Should().Be(2);
        job.Payload.Should().Contain("schedule");
        job.Status.Should().Be(JobStatus.Pending);
        job.IdempotencyKey.Should().EndWith($"{due.UtcDateTime:yyyyMMddTHHmmssZ}");

        reloaded.LastJobId.Should().Be(job.Id);
        reloaded.LastRunAt.Should().BeCloseTo(due, TimeSpan.FromMilliseconds(1));
        reloaded.NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow);
        reloaded.NextRunAt!.Value.Minute.Should().Match(m => m % 5 == 0);
    }

    [Fact]
    public async Task Schedules_that_are_not_due_or_paused_do_nothing()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var future = await AddScheduleAsync(type, "* * * * *", DateTimeOffset.UtcNow.AddMinutes(5));
        var paused = await AddScheduleAsync(type, "* * * * *", DateTimeOffset.UtcNow.AddMinutes(-5), paused: true);

        await FireAsync();

        (await LoadAsync(future.Id)).Jobs.Should().BeEmpty();
        (await LoadAsync(paused.Id)).Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Missed_occurrences_collapse_into_a_single_catch_up_run()
    {
        // The scheduler was down for three hours of an every-minute schedule.
        var type = await fixture.RegisterJobTypeAsync();
        var schedule = await AddScheduleAsync(type, "* * * * *", DateTimeOffset.UtcNow.AddHours(-3));

        await FireAsync();
        await FireAsync();

        var (reloaded, jobs) = await LoadAsync(schedule.Id);
        jobs.Should().ContainSingle();
        reloaded.NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow).And.BeBefore(DateTimeOffset.UtcNow.AddMinutes(1.1));
    }

    [Fact]
    public async Task Disabled_job_type_skips_the_run_but_keeps_the_schedule_moving()
    {
        var type = await fixture.RegisterJobTypeAsync();
        var schedule = await AddScheduleAsync(type, "* * * * *", DateTimeOffset.UtcNow.AddSeconds(-5));
        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReeveDbContext>();
            (await db.JobTypes.SingleAsync(t => t.Type == type)).Disable();
            await db.SaveChangesAsync();
        }

        await FireAsync();

        var (reloaded, jobs) = await LoadAsync(schedule.Id);
        jobs.Should().BeEmpty();
        reloaded.LastJobId.Should().BeNull();
        reloaded.NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Concurrent_schedulers_fire_each_occurrence_exactly_once()
    {
        const int scheduleCount = 25;
        var type = await fixture.RegisterJobTypeAsync();
        var schedules = new List<Schedule>();
        for (var i = 0; i < scheduleCount; i++)
            schedules.Add(await AddScheduleAsync(type, "0 0 1 1 *", DateTimeOffset.UtcNow.AddSeconds(-1)));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runners = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            for (var pass = 0; pass < 5; pass++)
                await FireAsync();
        })).ToList();
        gate.SetResult();
        await Task.WhenAll(runners);

        foreach (var schedule in schedules)
            (await LoadAsync(schedule.Id)).Jobs.Should().ContainSingle();
    }
}

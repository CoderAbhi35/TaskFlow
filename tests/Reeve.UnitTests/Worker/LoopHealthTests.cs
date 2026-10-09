using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Reeve.Infrastructure.Health;

namespace Reeve.UnitTests.Worker;

public class LoopHealthTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly ManualClock _clock = new();
    private readonly LoopMonitor _loops;
    private readonly HealthCheckService _health;

    public LoopHealthTests()
    {
        _loops = new LoopMonitor(_clock);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_loops);
        services.AddHealthChecks().AddLoopsAlive().AddLoopSucceeded("Heartbeat", TimeSpan.FromSeconds(30));
        _health = services.BuildServiceProvider().GetRequiredService<HealthCheckService>();
    }

    private async Task<HealthStatus> StatusAsync(string tag) =>
        (await _health.CheckHealthAsync(c => c.Tags.Contains(tag))).Status;

    [Fact]
    public async Task A_loop_that_keeps_passing_is_alive_even_when_every_pass_fails()
    {
        _loops.Register("Dispatcher", TimeSpan.FromMilliseconds(500));

        for (var i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            _loops.Beat("Dispatcher"); // failed pass: database down, for example
        }

        (await StatusAsync(HealthEndpoints.LiveTag)).Should().Be(HealthStatus.Healthy,
            "an outage must not get the pod restarted");
    }

    [Fact]
    public async Task A_loop_that_stops_passing_is_reported_stuck()
    {
        _loops.Register("Dispatcher", TimeSpan.FromMilliseconds(500));
        _loops.Register("Recovery", TimeSpan.FromSeconds(15));

        _clock.Advance(TimeSpan.FromSeconds(61));
        _loops.Beat("Recovery");

        var report = await _health.CheckHealthAsync(c => c.Tags.Contains(HealthEndpoints.LiveTag));
        report.Status.Should().Be(HealthStatus.Unhealthy);
        report.Entries["loops"].Description.Should().Be("Stuck: Dispatcher");
    }

    [Fact]
    public async Task Slow_loops_get_five_intervals_before_they_count_as_stuck()
    {
        _loops.Register("Sweeper", TimeSpan.FromSeconds(60));

        _clock.Advance(TimeSpan.FromSeconds(299));
        (await StatusAsync(HealthEndpoints.LiveTag)).Should().Be(HealthStatus.Healthy);

        _clock.Advance(TimeSpan.FromSeconds(2));
        (await StatusAsync(HealthEndpoints.LiveTag)).Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task A_loop_that_ended_on_shutdown_is_not_stuck()
    {
        _loops.Register("Kafka consumer", TimeSpan.FromMilliseconds(250));
        _loops.Unregister("Kafka consumer");

        _clock.Advance(TimeSpan.FromMinutes(5));

        (await StatusAsync(HealthEndpoints.LiveTag)).Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Ready_needs_a_recent_success_not_just_a_pass()
    {
        (await StatusAsync(HealthEndpoints.ReadyTag)).Should().Be(HealthStatus.Unhealthy, "the loop hasn't started");

        _loops.Register("Heartbeat", TimeSpan.FromSeconds(10));
        (await StatusAsync(HealthEndpoints.ReadyTag)).Should().Be(HealthStatus.Unhealthy, "it hasn't succeeded yet");

        _loops.Succeeded("Heartbeat");
        (await StatusAsync(HealthEndpoints.ReadyTag)).Should().Be(HealthStatus.Healthy);

        // The database goes away: passes keep failing, so the worker is alive but not ready.
        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            _loops.Beat("Heartbeat");
        }
        (await StatusAsync(HealthEndpoints.ReadyTag)).Should().Be(HealthStatus.Unhealthy);
        (await StatusAsync(HealthEndpoints.LiveTag)).Should().Be(HealthStatus.Healthy);

        _loops.Succeeded("Heartbeat");
        (await StatusAsync(HealthEndpoints.ReadyTag)).Should().Be(HealthStatus.Healthy);
    }
}

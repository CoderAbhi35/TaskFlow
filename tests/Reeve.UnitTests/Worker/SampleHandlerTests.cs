using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Application.Execution;
using Reeve.Worker.Handlers;

namespace Reeve.UnitTests.Worker;

public class SampleHandlerTests
{
    private static JobExecutionContext Context(string payload, int attempt = 1) =>
        new(Guid.CreateVersion7(), "GENERATE_REPORT", attempt, JsonDocument.Parse(payload).RootElement.Clone());

    private readonly GenerateReportHandler _handler = new(NullLogger<GenerateReportHandler>.Instance);

    [Fact]
    public async Task Valid_payload_succeeds()
    {
        await _handler.Invoking(h => h.ExecuteAsync(
                Context("""{"customerId": 1, "simulate": {"durationMs": 0}}"""), CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"customerId": "abc"}""")] // regression: used to throw InvalidOperationException (transient)
    [InlineData("""{"customerId": 1.5}""")]
    [InlineData("""{"customerId": null}""")]
    public async Task Invalid_payload_is_a_permanent_failure(string payload)
    {
        await _handler.Invoking(h => h.ExecuteAsync(Context(payload), CancellationToken.None))
            .Should().ThrowAsync<PermanentJobFailureException>().WithMessage("*customerId*");
    }

    [Fact]
    public async Task Simulated_transient_failure_can_be_limited_to_early_attempts()
    {
        const string payload = """{"customerId": 1, "simulate": {"durationMs": 0, "failure": "transient", "failAttempts": 2}}""";

        await _handler.Invoking(h => h.ExecuteAsync(Context(payload, attempt: 2), CancellationToken.None))
            .Should().ThrowAsync<IOException>();
        await _handler.Invoking(h => h.ExecuteAsync(Context(payload, attempt: 3), CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task Simulated_permanent_failure()
    {
        const string payload = """{"customerId": 1, "simulate": {"durationMs": 0, "failure": "permanent"}}""";

        await _handler.Invoking(h => h.ExecuteAsync(Context(payload), CancellationToken.None))
            .Should().ThrowAsync<PermanentJobFailureException>();
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await _handler.Invoking(h => h.ExecuteAsync(
                Context("""{"customerId": 1, "simulate": {"durationMs": 60000}}"""), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Registry_rejects_two_handlers_for_one_job_type()
    {
        var act = () => new JobHandlerRegistry([_handler, new GenerateReportHandler(NullLogger<GenerateReportHandler>.Instance)]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*GENERATE_REPORT*");
    }

    [Fact]
    public void Registry_exposes_supported_job_types()
    {
        var registry = new JobHandlerRegistry([
            _handler,
            new SendNotificationHandler(NullLogger<SendNotificationHandler>.Instance),
            new ProcessImageHandler(NullLogger<ProcessImageHandler>.Instance),
        ]);

        registry.JobTypes.Should().BeEquivalentTo("GENERATE_REPORT", "SEND_NOTIFICATION", "PROCESS_IMAGE");
        registry.TryGet("PROCESS_IMAGE", out var handler).Should().BeTrue();
        handler.Should().BeOfType<ProcessImageHandler>();
    }
}

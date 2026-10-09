using System.Text.Json;
using FluentAssertions;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Messages;
using Reeve.Contracts.Serialization;
using Reeve.Infrastructure.Messaging;

namespace Reeve.UnitTests.Messaging;

public class MessagingContractTests
{
    [Fact]
    public void Dispatch_message_wire_format_is_stable()
    {
        var message = new JobDispatchMessage(
            Guid.Parse("0190a000-0000-7000-8000-000000000001"), "GENERATE_REPORT", JobPriority.High,
            new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(message, ReeveJson.Options);

        json.Should().Be("""{"jobId":"0190a000-0000-7000-8000-000000000001","jobType":"GENERATE_REPORT","priority":"HIGH","dispatchedAt":"2026-09-24T12:00:00+00:00","schemaVersion":1}""");
        JsonSerializer.Deserialize<JobDispatchMessage>(json, ReeveJson.Options).Should().Be(message);
    }

    [Fact]
    public void Each_job_type_has_its_own_topic()
    {
        var options = new KafkaOptions { TopicPrefix = "reeve.jobs" };

        options.TopicFor("GENERATE_REPORT").Should().Be("reeve.jobs.GENERATE_REPORT");
    }
}

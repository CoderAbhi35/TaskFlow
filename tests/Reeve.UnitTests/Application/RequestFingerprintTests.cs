using System.Text.Json;
using FluentAssertions;
using Reeve.Application.Jobs;
using Reeve.Contracts.Jobs;

namespace Reeve.UnitTests.Application;

public class RequestFingerprintTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly CreateJobRequest Baseline = new(
        "GENERATE_REPORT", JobPriority.High, Json("""{"customerId":1}"""), new RetryPolicyDto(3, 30),
        new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Is_a_sha256_hex_string()
    {
        RequestFingerprint.Compute(Baseline).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Ignores_formatting_and_equivalent_spellings()
    {
        var reformatted = Baseline with
        {
            JobType = "  GENERATE_REPORT ",
            Payload = Json("""{ "customerId" :  1 }"""),
            ScheduledAt = new DateTimeOffset(2026, 10, 1, 14, 30, 0, TimeSpan.FromHours(5.5)), // same instant
        };

        RequestFingerprint.Compute(reformatted).Should().Be(RequestFingerprint.Compute(Baseline));
    }

    [Fact]
    public void Omitted_priority_equals_explicit_default()
    {
        RequestFingerprint.Compute(Baseline with { Priority = null })
            .Should().Be(RequestFingerprint.Compute(Baseline with { Priority = JobPriority.Normal }));
    }

    [Fact]
    public void Changes_when_any_content_changes()
    {
        var variants = new[]
        {
            Baseline with { JobType = "SEND_NOTIFICATION" },
            Baseline with { Priority = JobPriority.Low },
            Baseline with { Payload = Json("""{"customerId":2}""") },
            Baseline with { RetryPolicy = new RetryPolicyDto(4, 30) },
            Baseline with { ScheduledAt = null },
        };

        variants.Select(RequestFingerprint.Compute)
            .Append(RequestFingerprint.Compute(Baseline))
            .Should().OnlyHaveUniqueItems();
    }
}

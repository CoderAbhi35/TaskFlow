using System.Text.Json;
using FluentAssertions;
using FluentValidation.TestHelper;
using Reeve.Application.Jobs;
using Reeve.Contracts.Jobs;

namespace Reeve.UnitTests.Application;

public class CreateJobRequestValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly CreateJobRequestValidator _validator = new(new FixedTimeProvider(Now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Minimal_request_is_valid()
    {
        _validator.TestValidate(new CreateJobRequest("GENERATE_REPORT")).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad type")]
    [InlineData("-leading-dash")]
    public void Job_type_is_required_and_well_formed(string? type)
    {
        _validator.TestValidate(new CreateJobRequest(type)).ShouldHaveValidationErrorFor(r => r.JobType);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Payload_must_be_an_object(string payload)
    {
        _validator.TestValidate(new CreateJobRequest("X", Payload: Json(payload)))
            .ShouldHaveValidationErrorFor(nameof(CreateJobRequest.Payload));
    }

    [Fact]
    public void Payload_size_is_limited()
    {
        var atLimit = Json($$"""{"d":"{{new string('x', CreateJobRequestValidator.MaxPayloadBytes - 8)}}"}""");
        var overLimit = Json($$"""{"d":"{{new string('x', CreateJobRequestValidator.MaxPayloadBytes)}}"}""");

        _validator.TestValidate(new CreateJobRequest("X", Payload: atLimit)).ShouldNotHaveAnyValidationErrors();
        _validator.TestValidate(new CreateJobRequest("X", Payload: overLimit))
            .ShouldHaveValidationErrorFor(nameof(CreateJobRequest.Payload));
    }

    [Fact]
    public void Retry_policy_is_bounded()
    {
        var result = _validator.TestValidate(new CreateJobRequest("X", RetryPolicy: new RetryPolicyDto(11, 0)));

        result.ShouldHaveValidationErrorFor("RetryPolicy.MaxRetries");
        result.ShouldHaveValidationErrorFor("RetryPolicy.BackoffSeconds");
    }

    [Fact]
    public void Schedule_is_limited_to_a_year_ahead()
    {
        _validator.TestValidate(new CreateJobRequest("X", ScheduledAt: Now.AddDays(365))).ShouldNotHaveAnyValidationErrors();
        _validator.TestValidate(new CreateJobRequest("X", ScheduledAt: Now.AddDays(366)))
            .ShouldHaveValidationErrorFor(r => r.ScheduledAt);
    }

    [Fact]
    public void Schedule_in_the_past_means_run_now()
    {
        _validator.TestValidate(new CreateJobRequest("X", ScheduledAt: Now.AddDays(-1))).ShouldNotHaveAnyValidationErrors();
    }
}

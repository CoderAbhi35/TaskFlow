using FluentAssertions;
using Reeve.Domain;
using Reeve.Domain.Jobs;

namespace Reeve.UnitTests.Domain;

public class RetryPolicyTests
{
    /// <summary>Returns a fixed value so jitter can be pinned.</summary>
    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private static readonly Random NoJitter = new FixedRandom(0.5); // maps to a jitter factor of exactly 1

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 45)]
    [InlineData(4, 135)]
    public void Delay_grows_by_factor_of_three(int retryNumber, int expectedSeconds)
    {
        var policy = new RetryPolicy(maxRetries: 5, backoffSeconds: 5);

        policy.GetDelay(retryNumber, NoJitter).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Delay_is_capped()
    {
        var policy = new RetryPolicy(maxRetries: 10, backoffSeconds: 600);

        policy.GetDelay(10, new FixedRandom(0.999)).Should().Be(RetryPolicy.MaxDelay);
    }

    [Theory]
    [InlineData(0.0, 80)]
    [InlineData(0.999999, 120)]
    public void Jitter_stays_within_twenty_percent(double sample, int expectedSeconds)
    {
        var policy = new RetryPolicy(maxRetries: 3, backoffSeconds: 100);

        policy.GetDelay(1, new FixedRandom(sample)).TotalSeconds
            .Should().BeApproximately(expectedSeconds, 0.001);
    }

    [Fact]
    public void Jitter_spreads_simultaneous_retries()
    {
        var policy = new RetryPolicy(maxRetries: 3, backoffSeconds: 30);
        var random = new Random(7);

        var delays = Enumerable.Range(0, 50).Select(_ => policy.GetDelay(1, random)).Distinct();

        delays.Should().HaveCountGreaterThan(40);
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(RetryPolicy.MaxAllowedRetries + 1, 5)]
    [InlineData(3, 0)]
    [InlineData(3, 3601)]
    public void Rejects_out_of_range_values(int maxRetries, int backoffSeconds)
    {
        var act = () => new RetryPolicy(maxRetries, backoffSeconds);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Retry_number_starts_at_one()
    {
        var act = () => RetryPolicy.Default.GetDelay(0, NoJitter);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

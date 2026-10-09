namespace Reeve.Domain.Jobs;

/// <summary>
/// Bounded exponential backoff with jitter: base, base*3, base*9, ... capped at <see cref="MaxDelay"/>,
/// then randomised by ±<see cref="JitterRatio"/> so that jobs failing together do not retry together.
/// </summary>
public sealed record RetryPolicy
{
    public const int MaxAllowedRetries = 10;
    public const double BackoffMultiplier = 3.0;
    public const double JitterRatio = 0.2;
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    public static RetryPolicy Default { get; } = new(maxRetries: 3, backoffSeconds: 5);
    public static RetryPolicy None { get; } = new(maxRetries: 0, backoffSeconds: 1);

    public int MaxRetries { get; }
    public int BackoffSeconds { get; }

    public RetryPolicy(int maxRetries, int backoffSeconds)
    {
        if (maxRetries is < 0 or > MaxAllowedRetries)
            throw new DomainException($"maxRetries must be between 0 and {MaxAllowedRetries}.");
        if (backoffSeconds < 1 || backoffSeconds > MaxDelay.TotalSeconds)
            throw new DomainException($"backoffSeconds must be between 1 and {MaxDelay.TotalSeconds}.");

        MaxRetries = maxRetries;
        BackoffSeconds = backoffSeconds;
    }

    /// <param name="retryNumber">1 for the first retry, 2 for the second, and so on.</param>
    /// <param name="random">Source of jitter; pass a seeded instance for deterministic tests.</param>
    public TimeSpan GetDelay(int retryNumber, Random random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryNumber, 1);
        ArgumentNullException.ThrowIfNull(random);

        var seconds = Math.Min(
            BackoffSeconds * Math.Pow(BackoffMultiplier, retryNumber - 1),
            MaxDelay.TotalSeconds);

        var jitterFactor = 1 + JitterRatio * (random.NextDouble() * 2 - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds * jitterFactor, MaxDelay.TotalSeconds));
    }
}

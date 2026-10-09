namespace Reeve.Domain.Jobs;

/// <summary>
/// Execution policy for a job type. Only registered, enabled types are accepted, so the platform never
/// runs arbitrary work described by a payload.
/// </summary>
public sealed class JobTypeDefinition
{
    public string Type { get; private set; } = null!;
    public bool Enabled { get; private set; }
    public int TimeoutSeconds { get; private set; }
    public int MaxRetries { get; private set; }

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    private JobTypeDefinition() { } // EF Core

    public JobTypeDefinition(string type, int timeoutSeconds, int maxRetries, bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > Job.MaxTypeLength)
            throw new DomainException($"Job type must be 1-{Job.MaxTypeLength} characters.");
        if (timeoutSeconds < 1)
            throw new DomainException("Timeout must be at least 1 second.");
        if (maxRetries is < 0 or > RetryPolicy.MaxAllowedRetries)
            throw new DomainException($"maxRetries must be between 0 and {RetryPolicy.MaxAllowedRetries}.");

        Type = type.Trim();
        TimeoutSeconds = timeoutSeconds;
        MaxRetries = maxRetries;
        Enabled = enabled;
    }

    public void Enable() => Enabled = true;

    public void Disable() => Enabled = false;
}

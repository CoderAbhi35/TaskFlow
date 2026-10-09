namespace Reeve.Domain.Jobs;

/// <summary>
/// Marks that a named side effect of a job has been carried out, so a retry of the job (after a
/// crash, timeout or later failure) can skip it instead of doing it again.
/// </summary>
public sealed class JobEffect
{
    public const int MaxKeyLength = 200;

    public Guid JobId { get; private set; }
    public string Key { get; private set; } = null!;
    public int AttemptNumber { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    private JobEffect() { } // EF Core

    public JobEffect(Guid jobId, string key, int attemptNumber, DateTimeOffset recordedAt)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength)
            throw new DomainException($"Effect key must be 1-{MaxKeyLength} characters.");

        JobId = jobId;
        Key = key;
        AttemptNumber = attemptNumber;
        RecordedAt = recordedAt.ToUniversalTime();
    }
}

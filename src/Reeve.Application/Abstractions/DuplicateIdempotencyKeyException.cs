namespace Reeve.Application.Abstractions;

/// <summary>A concurrent request inserted a job with the same idempotency key first.</summary>
public sealed class DuplicateIdempotencyKeyException(string idempotencyKey, Exception innerException)
    : Exception($"A job with idempotency key '{idempotencyKey}' already exists.", innerException)
{
    public string IdempotencyKey { get; } = idempotencyKey;
}

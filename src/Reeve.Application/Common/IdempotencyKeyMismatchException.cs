namespace Reeve.Application.Common;

/// <summary>The idempotency key was already used for a request with different content.</summary>
public sealed class IdempotencyKeyMismatchException(string idempotencyKey)
    : Exception($"Idempotency key '{idempotencyKey}' was already used with a different request.");

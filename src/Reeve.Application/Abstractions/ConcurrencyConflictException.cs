namespace Reeve.Application.Abstractions;

/// <summary>
/// An optimistic concurrency check failed: the entity was modified by someone else after it was loaded.
/// Callers should reload and re-evaluate rather than overwrite.
/// </summary>
public sealed class ConcurrencyConflictException(string message, Exception innerException)
    : Exception(message, innerException);

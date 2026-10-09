namespace Reeve.ApiTests.Infrastructure;

/// <summary>The error contract clients rely on (RFC 9457 plus Reeve extensions).</summary>
public sealed record ProblemResponse(
    int Status,
    string Title,
    string? Detail,
    string Code,
    string? CorrelationId,
    Dictionary<string, string[]>? Errors);

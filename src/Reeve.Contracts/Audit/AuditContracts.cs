using System.Text.Json;

namespace Reeve.Contracts.Audit;

public sealed record AuditEventResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string EntityType,
    string EntityId,
    string? CorrelationId,
    JsonElement? Details);

/// <summary>Query-string filters for <c>GET /api/v1/audit</c>. Newest first; page with <c>cursor</c>.</summary>
public sealed class SearchAuditRequest
{
    public string? Actor { get; init; }
    public string? Action { get; init; }
    public string? EntityId { get; init; }
    public int? Limit { get; init; }
    public string? Cursor { get; init; }
}

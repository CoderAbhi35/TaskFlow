namespace Reeve.Domain.Audit;

/// <summary>
/// An immutable record that someone did something: who (actor), what (action), to what (entity), and
/// the request it came from (correlation ID). Written in the same transaction as the change itself.
/// </summary>
public sealed class AuditEvent
{
    public const int MaxActorLength = 200;
    public const int MaxActionLength = 64;
    public const int MaxEntityTypeLength = 32;
    public const int MaxEntityIdLength = 200;

    public Guid Id { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string Actor { get; private set; } = null!;
    public string Action { get; private set; } = null!;
    public string EntityType { get; private set; } = null!;
    public string EntityId { get; private set; } = null!;
    public string? CorrelationId { get; private set; }

    /// <summary>Small JSON object with context (e.g. job type). Never payloads or secrets.</summary>
    public string? Details { get; private set; }

    private AuditEvent() { } // EF Core

    public AuditEvent(string actor, string action, string entityType, string entityId,
        DateTimeOffset occurredAt, string? correlationId = null, string? details = null)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new DomainException("An audit event needs an actor.");
        if (string.IsNullOrWhiteSpace(action)) throw new DomainException("An audit event needs an action.");

        occurredAt = occurredAt.ToUniversalTime();
        Id = Guid.CreateVersion7(occurredAt);
        OccurredAt = occurredAt;
        Actor = Truncate(actor, MaxActorLength);
        Action = Truncate(action, MaxActionLength);
        EntityType = Truncate(entityType, MaxEntityTypeLength);
        EntityId = Truncate(entityId, MaxEntityIdLength);
        CorrelationId = correlationId;
        Details = details;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

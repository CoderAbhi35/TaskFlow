using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Reeve.Application.Abstractions;
using Reeve.Application.Jobs;
using Reeve.Contracts.Audit;
using Reeve.Contracts.Common;
using Reeve.Contracts.Serialization;
using Reeve.Domain.Audit;

namespace Reeve.Infrastructure.Persistence;

/// <summary>Adds audit events to the current DbContext, so they commit (or roll back) with the change.</summary>
internal sealed class AuditLog(ReeveDbContext db, IRequestContext request, TimeProvider time) : IAuditLog
{
    public void Record(string action, string entityType, string entityId, object? details = null, string? actor = null) =>
        db.AuditEvents.Add(new AuditEvent(
            actor ?? request.Actor, action, entityType, entityId, time.GetUtcNow(), request.CorrelationId,
            details is null ? null : JsonSerializer.Serialize(details, ReeveJson.Options)));
}

public interface IAuditQueries
{
    Task<PagedResponse<AuditEventResponse>> SearchAsync(SearchAuditRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Newest first, keyset-paged on the time-ordered event ID (like job search).</summary>
internal sealed class AuditQueries(ReeveDbContext db) : IAuditQueries
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public async Task<PagedResponse<AuditEventResponse>> SearchAsync(
        SearchAuditRequest request, CancellationToken cancellationToken = default)
    {
        var limit = request.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaxLimit)
            throw Invalid("limit", $"Limit must be between 1 and {MaxLimit}.");

        Guid? before = null;
        if (request.Cursor is not null)
        {
            if (!Guid.TryParseExact(request.Cursor, "N", out var cursor))
                throw Invalid("cursor", "Cursor is not valid.");
            before = cursor;
        }

        var query = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Actor))
            query = query.Where(e => e.Actor == request.Actor);
        if (!string.IsNullOrWhiteSpace(request.Action))
            query = query.Where(e => e.Action == request.Action);
        if (!string.IsNullOrWhiteSpace(request.EntityId))
            query = query.Where(e => e.EntityId == request.EntityId);
        if (before is { } b)
            query = query.Where(e => e.Id.CompareTo(b) < 0);

        var rows = await query.OrderByDescending(e => e.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var page = rows.Take(limit).Select(e => new AuditEventResponse(
            e.Id, e.OccurredAt, e.Actor, e.Action, e.EntityType, e.EntityId, e.CorrelationId,
            e.Details is null ? null : JobMappings.ParsePayload(e.Details))).ToList();

        return new PagedResponse<AuditEventResponse>(page, rows.Count > limit ? page[^1].Id.ToString("N") : null);
    }

    private static ValidationException Invalid(string field, string message) =>
        new([new ValidationFailure(field, message)]);
}

/// <summary>The actor for work that no user asked for directly (workers, the scheduler, tests).</summary>
internal sealed class SystemRequestContext : IRequestContext
{
    public string Actor => IRequestContext.System;
    public string? CorrelationId => null;
}

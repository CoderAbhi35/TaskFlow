using FluentValidation;
using FluentValidation.Results;
using Reeve.Application.Common;
using Reeve.Contracts.Jobs;
using DomainModel = Reeve.Domain.Jobs;

namespace Reeve.Application.Jobs;

/// <summary>
/// Validated search filters. Results are newest first (UUIDv7 IDs are time-ordered), and the cursor is
/// the last ID of the previous page, so pages stay stable while new jobs are being created.
/// </summary>
public sealed record JobSearchCriteria(
    IReadOnlyList<DomainModel.JobStatus> Statuses,
    string? JobType,
    DomainModel.JobPriority? Priority,
    DateTimeOffset? CreatedAfter,
    DateTimeOffset? CreatedBefore,
    int Limit,
    Guid? Before)
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    public static JobSearchCriteria From(SearchJobsRequest request)
    {
        var failures = new List<ValidationFailure>();

        var statuses = new List<DomainModel.JobStatus>();
        foreach (var value in request.Status ?? [])
        {
            if (TryParseWireEnum<JobStatus>(value, out var status))
                statuses.Add(EnumMapper.Map<JobStatus, DomainModel.JobStatus>(status));
            else
                failures.Add(new ValidationFailure("status", $"Unknown status '{value}'."));
        }

        DomainModel.JobPriority? priority = null;
        if (request.Priority is not null)
        {
            if (TryParseWireEnum<JobPriority>(request.Priority, out var parsed))
                priority = EnumMapper.Map<JobPriority, DomainModel.JobPriority>(parsed);
            else
                failures.Add(new ValidationFailure("priority", $"Unknown priority '{request.Priority}'."));
        }

        var limit = request.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaxLimit)
            failures.Add(new ValidationFailure("limit", $"Limit must be between 1 and {MaxLimit}."));

        Guid? before = null;
        if (request.Cursor is not null)
        {
            if (Guid.TryParseExact(request.Cursor, "N", out var cursor))
                before = cursor;
            else
                failures.Add(new ValidationFailure("cursor", "Cursor is not valid."));
        }

        if (request.CreatedAfter > request.CreatedBefore)
            failures.Add(new ValidationFailure("createdAfter", "createdAfter must not be later than createdBefore."));

        if (failures.Count > 0)
            throw new ValidationException(failures);

        return new JobSearchCriteria(
            statuses.Distinct().ToList(),
            string.IsNullOrWhiteSpace(request.JobType) ? null : request.JobType.Trim(),
            priority,
            request.CreatedAfter?.ToUniversalTime(),
            request.CreatedBefore?.ToUniversalTime(),
            limit,
            before);
    }

    public static string EncodeCursor(Guid lastId) => lastId.ToString("N");

    /// <summary>Accepts the wire spelling (<c>DEAD_LETTERED</c>) case-insensitively and rejects numbers.</summary>
    private static bool TryParseWireEnum<T>(string value, out T result) where T : struct, Enum
    {
        result = default;
        var name = value.Replace("_", "");
        return name.Length > 0
            && name.All(char.IsLetter)
            && Enum.TryParse(name, ignoreCase: true, out result);
    }
}

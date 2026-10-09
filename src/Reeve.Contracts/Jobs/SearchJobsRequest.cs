namespace Reeve.Contracts.Jobs;

/// <summary>Query-string filters for <c>GET /api/v1/jobs</c>. All filters are optional and combined with AND.</summary>
public sealed class SearchJobsRequest
{
    /// <summary>One or more statuses, e.g. <c>?status=FAILED&amp;status=DEAD_LETTERED</c>.</summary>
    public string[]? Status { get; init; }

    public string? JobType { get; init; }

    public string? Priority { get; init; }

    public DateTimeOffset? CreatedAfter { get; init; }

    public DateTimeOffset? CreatedBefore { get; init; }

    /// <summary>Page size, 1-100. Defaults to 25.</summary>
    public int? Limit { get; init; }

    /// <summary>The <c>nextCursor</c> from the previous page.</summary>
    public string? Cursor { get; init; }
}

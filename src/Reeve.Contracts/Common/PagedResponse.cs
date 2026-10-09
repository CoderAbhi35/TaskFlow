namespace Reeve.Contracts.Common;

/// <summary>
/// One page of results. Pass <see cref="NextCursor"/> back as <c>cursor</c> to get the next page;
/// it is null on the last page. Cursors are opaque and remain stable while new items are added.
/// </summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, string? NextCursor);

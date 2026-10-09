namespace Reeve.Domain.Jobs;

/// <summary>
/// Stored as its integer value so queue selection can simply ORDER BY priority DESC.
/// </summary>
public enum JobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

using Cronos;

namespace Reeve.Application.Schedules;

/// <summary>
/// Standard 5-field cron expressions (minute granularity) evaluated in a named time zone, so
/// "0 9 * * *" in Europe/London stays 09:00 local time across daylight-saving changes.
/// </summary>
public static class CronSchedule
{
    public const string DefaultTimeZone = "UTC";

    public static bool IsValidExpression(string? expression) => TryParse(expression, out _);

    /// <summary>Accepts IANA ids ("Asia/Kolkata") and, on Windows, Windows ids too.</summary>
    public static bool IsValidTimeZone(string? timeZone) => TryFindTimeZone(timeZone, out _);

    /// <summary>The first occurrence strictly after <paramref name="after"/>, or null if there is none.</summary>
    public static DateTimeOffset? NextAfter(string expression, string timeZone, DateTimeOffset after)
    {
        if (!TryParse(expression, out var cron))
            throw new ArgumentException($"Invalid cron expression '{expression}'.", nameof(expression));
        if (!TryFindTimeZone(timeZone, out var zone))
            throw new ArgumentException($"Unknown time zone '{timeZone}'.", nameof(timeZone));

        return cron!.GetNextOccurrence(after, zone!);
    }

    private static bool TryParse(string? expression, out CronExpression? cron)
    {
        cron = null;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        try
        {
            cron = CronExpression.Parse(expression.Trim(), CronFormat.Standard);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    private static bool TryFindTimeZone(string? timeZone, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(timeZone))
            return false;

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZone.Trim(), out zone);
    }
}

using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>The short form in which the pages of an automation show a time in the zone of the viewer.</summary>
internal static class AutomationTime
{
    /// <summary>
    /// The time of day for a time on the current day of the viewer, otherwise the date and the time; the placeholder of
    /// <paramref name="timeZone"/> while its zone is not resolved.
    /// </summary>
    public static string Format(ITimeZoneDisplay timeZone, DateTimeOffset value, DateTimeOffset now) =>
        FormatZoned(timeZone, value, now, "HH:mm", "g");

    /// <summary>Like <see cref="Format"/>, with the seconds, for times that can lie within one minute of each other.</summary>
    public static string FormatWithSeconds(ITimeZoneDisplay timeZone, DateTimeOffset value, DateTimeOffset now) =>
        FormatZoned(timeZone, value, now, "HH:mm:ss", "G");

    private static string FormatZoned(ITimeZoneDisplay timeZone, DateTimeOffset value, DateTimeOffset now, string timeFormat, string dateTimeFormat)
    {
        if (!timeZone.IsResolved)
        {
            return timeZone.Placeholder;
        }

        var zoned = timeZone.ToZoned(value);
        return zoned.Date == timeZone.ToZoned(now).Date ? zoned.ToString(timeFormat) : zoned.ToString(dateTimeFormat);
    }
}

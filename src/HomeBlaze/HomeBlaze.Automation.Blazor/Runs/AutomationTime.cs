using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>The short form in which the pages of an automation show a time in the zone of the viewer.</summary>
public static class AutomationTime
{
    /// <summary>
    /// The time of day for a time on the current day of the viewer, otherwise the date and the time; the placeholder of
    /// <paramref name="timeZone"/> while its zone is not resolved.
    /// </summary>
    public static string Format(ITimeZoneDisplay timeZone, DateTimeOffset value, DateTimeOffset now)
    {
        if (!timeZone.IsResolved)
        {
            return timeZone.Placeholder;
        }

        var zoned = timeZone.ToZoned(value);
        return zoned.Date == timeZone.ToZoned(now).Date ? zoned.ToString("HH:mm") : zoned.ToString("g");
    }
}

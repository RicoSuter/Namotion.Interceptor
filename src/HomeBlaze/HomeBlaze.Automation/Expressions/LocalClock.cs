namespace HomeBlaze.Automation.Expressions;

/// <summary>The local time of day that automation expressions read as <c>time</c>, and the instants its offset changes.</summary>
internal static class LocalClock
{
    /// <summary>
    /// Gets the local time of day of the instant, which never moves backwards within a day: in the hour that repeats when
    /// the clocks go back, it stays at the last time of day before they went back until the local time passes it again,
    /// so that a time of day in that hour is passed once.
    /// </summary>
    public static TimeSpan GetTimeOfDay(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        var offset = timeZone.GetUtcOffset(instant);
        var timeOfDay = instant.ToOffset(offset).TimeOfDay;
        if (!timeZone.SupportsDaylightSavingTime || !timeZone.IsAmbiguousTime(instant))
        {
            return timeOfDay;
        }

        var offsetBefore = timeZone.GetAmbiguousTimeOffsets(instant).Max();
        if (offset == offsetBefore)
        {
            return timeOfDay;
        }

        // The clocks went back less than the difference of the offsets ago.
        var wentBackAt = GetOffsetChange(timeZone, offsetBefore, instant - (offsetBefore - offset), instant) ?? instant;
        return wentBackAt.ToOffset(offsetBefore).AddTicks(-1).TimeOfDay;
    }

    /// <summary>
    /// Gets the first instant after <paramref name="now"/> with another offset than <paramref name="offset"/>, at the
    /// latest at <paramref name="limit"/>; null without one. A time zone changes its offset at most once in between.
    /// </summary>
    public static DateTimeOffset? GetOffsetChange(TimeZoneInfo timeZone, TimeSpan offset, DateTimeOffset now, DateTimeOffset limit)
    {
        if (timeZone.GetUtcOffset(limit) == offset)
        {
            return null;
        }

        var unchanged = now.UtcTicks;
        var changed = limit.UtcTicks;
        while (changed - unchanged > 1)
        {
            var middle = unchanged + ((changed - unchanged) / 2);
            if (timeZone.GetUtcOffset(new DateTimeOffset(middle, TimeSpan.Zero)) == offset)
            {
                unchanged = middle;
            }
            else
            {
                changed = middle;
            }
        }

        return new DateTimeOffset(changed, TimeSpan.Zero);
    }
}

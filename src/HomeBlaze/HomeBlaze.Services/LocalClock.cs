namespace HomeBlaze.Services;

/// <summary>The local time of day of an instant in a time zone, and the instants at which the zone changes its offset.</summary>
public static class LocalClock
{
    /// <summary>
    /// Gets the local time of day of the instant, which never moves backwards within a day: in the time that repeats when
    /// the clocks go back, it stays at the last time of day before they went back until the local time passes it again,
    /// so that each time of day in that time is passed once.
    /// </summary>
    /// <param name="instant">The instant.</param>
    /// <param name="timeZone">The time zone whose local time is read.</param>
    /// <returns>The local time of day, from zero to less than a day.</returns>
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
    /// Gets the first instant after <paramref name="start"/>, to the tick, at which the time zone has another offset than
    /// <paramref name="offset"/>, at the latest <paramref name="limit"/>.
    /// </summary>
    /// <param name="timeZone">The time zone.</param>
    /// <param name="offset">The offset of the time zone at <paramref name="start"/>.</param>
    /// <param name="start">The instant to search from.</param>
    /// <param name="limit">The last instant to search, after <paramref name="start"/>.</param>
    /// <returns>The instant in UTC, or null when the time zone has <paramref name="offset"/> at <paramref name="limit"/>.</returns>
    /// <remarks>The time zone must change its offset at most once between <paramref name="start"/> and <paramref name="limit"/>.</remarks>
    public static DateTimeOffset? GetOffsetChange(TimeZoneInfo timeZone, TimeSpan offset, DateTimeOffset start, DateTimeOffset limit)
    {
        if (timeZone.GetUtcOffset(limit) == offset)
        {
            return null;
        }

        var unchanged = start.UtcTicks;
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

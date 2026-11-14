namespace HomeBlaze.History.Abstractions;

/// <summary>
/// A calendar period a history bucket can span. A week is an ISO week, starting Monday.
/// </summary>
public enum HistoryCalendarUnit
{
    Day,
    Week,
    Month,
    Year
}

/// <summary>
/// The bucket of a bucketed history query: either a fixed size anchored at the Unix epoch, or a calendar period
/// in a time zone, whose length varies with the calendar and daylight saving time. Converts implicitly from a
/// <see cref="TimeSpan"/>.
/// </summary>
public readonly record struct HistoryBucket
{
    private HistoryBucket(TimeSpan? fixedSize, HistoryCalendarUnit? calendarUnit, TimeZoneInfo? timeZone)
    {
        FixedSize = fixedSize;
        CalendarUnit = calendarUnit;
        TimeZone = timeZone;
    }

    /// <summary>Gets the size of a fixed bucket, or null for a calendar bucket.</summary>
    public TimeSpan? FixedSize { get; }

    /// <summary>Gets the period of a calendar bucket, or null for a fixed bucket.</summary>
    public HistoryCalendarUnit? CalendarUnit { get; }

    /// <summary>Gets the time zone a calendar bucket is aligned in, or null for a fixed bucket.</summary>
    public TimeZoneInfo? TimeZone { get; }

    /// <summary>Creates a fixed bucket. <see cref="HistoryQuery.Validate"/> rejects a size that is not positive.</summary>
    public static HistoryBucket Fixed(TimeSpan size) => new(size, null, null);

    /// <summary>Creates a calendar bucket aligned to local period starts in <paramref name="timeZone"/>.</summary>
    public static HistoryBucket Calendar(HistoryCalendarUnit unit, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return new HistoryBucket(null, unit, timeZone);
    }

    /// <summary>Converts a size to a fixed bucket.</summary>
    public static implicit operator HistoryBucket(TimeSpan size) => Fixed(size);

    /// <inheritdoc />
    public override string ToString() =>
        FixedSize is { } size ? size.ToString() : $"{CalendarUnit} ({TimeZone?.Id})";
}

namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Bucket alignment. Fixed buckets are epoch-anchored, matching Postgres time_bucket; calendar buckets start at
/// local period starts in a time zone. All backends produce buckets at identical timestamps for the same bucket and
/// sample timestamps, so the merger never interleaves duplicates.
/// </summary>
public static class BucketAlignment
{
    /// <summary>
    /// Returns the start of the bucket containing <paramref name="ts"/> for the given
    /// <paramref name="bucket"/> size, anchored at the Unix epoch.
    /// </summary>
    public static DateTimeOffset BucketStart(DateTimeOffset ts, TimeSpan bucket)
    {
        if (bucket <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bucket), "The bucket size must be positive.");
        }

        var ticksFromEpoch = (ts - DateTimeOffset.UnixEpoch).Ticks;
        var quotient = Math.DivRem(ticksFromEpoch, bucket.Ticks, out var remainder);
        if (remainder < 0)
        {
            quotient--;
        }

        return DateTimeOffset.UnixEpoch.AddTicks(quotient * bucket.Ticks);
    }

    /// <summary>
    /// Returns the first aligned bucket that can contribute to the newest
    /// <paramref name="maxBuckets"/> buckets in <paramref name="from"/>..<paramref name="to"/>.
    /// This bounds query planning and aggregation work to the output budget.
    /// </summary>
    public static DateTimeOffset FirstBucketStart(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucket,
        int maxBuckets)
    {
        if (from >= to)
        {
            throw new ArgumentException("The start timestamp must be before the end timestamp.", nameof(from));
        }

        if (bucket <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bucket), "The bucket size must be positive.");
        }

        if (maxBuckets <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBuckets), "The maximum bucket count must be positive.");
        }

        var first = BucketStart(from, bucket);
        var spanTicks = (to - first).Ticks;
        var totalBucketCount = 1L + (spanTicks - 1L) / bucket.Ticks;
        var skippedBucketCount = Math.Max(0L, totalBucketCount - maxBuckets);
        return first.AddTicks(skippedBucketCount * bucket.Ticks);
    }

    /// <summary>
    /// Returns the start of the bucket containing <paramref name="timestamp"/>, as a UTC instant. A fixed bucket is
    /// anchored at the Unix epoch; a calendar bucket starts at local midnight of its period in the bucket's time zone,
    /// or at the first instant after it when the clocks skip midnight.
    /// </summary>
    public static DateTimeOffset Start(DateTimeOffset timestamp, HistoryBucket bucket)
    {
        if (bucket.FixedSize is { } size)
        {
            return BucketStart(timestamp, size);
        }

        var (unit, timeZone) = GetCalendar(bucket);
        return ToInstant(PeriodStartDate(TimeZoneInfo.ConvertTime(timestamp, timeZone).DateTime, unit), timeZone);
    }

    /// <summary>
    /// Returns the start of the bucket after the one starting at <paramref name="bucketStart"/>, as a UTC instant.
    /// </summary>
    public static DateTimeOffset Next(DateTimeOffset bucketStart, HistoryBucket bucket)
    {
        if (bucket.FixedSize is { } size)
        {
            if (size <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(bucket), "The bucket size must be positive.");
            }

            return bucketStart + size;
        }

        var (unit, timeZone) = GetCalendar(bucket);
        var periodStart = PeriodStartDate(TimeZoneInfo.ConvertTime(bucketStart, timeZone).DateTime, unit);
        var nextPeriodStart = unit switch
        {
            HistoryCalendarUnit.Day => periodStart.AddDays(1),
            HistoryCalendarUnit.Week => periodStart.AddDays(7),
            HistoryCalendarUnit.Month => periodStart.AddMonths(1),
            _ => periodStart.AddYears(1)
        };
        return ToInstant(nextPeriodStart, timeZone);
    }

    /// <summary>
    /// Returns the first bucket that can contribute to the newest <paramref name="maxBuckets"/> buckets in
    /// <paramref name="from"/>..<paramref name="to"/>, for a fixed or calendar bucket.
    /// </summary>
    public static DateTimeOffset FirstBucketStart(
        DateTimeOffset from,
        DateTimeOffset to,
        HistoryBucket bucket,
        int maxBuckets)
    {
        if (bucket.FixedSize is { } size)
        {
            return FirstBucketStart(from, to, size, maxBuckets);
        }

        if (from >= to)
        {
            throw new ArgumentException("The start timestamp must be before the end timestamp.", nameof(from));
        }

        if (maxBuckets <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBuckets), "The maximum bucket count must be positive.");
        }

        // Walks back from the newest bucket, so the cost is bounded by maxBuckets, not by the range.
        var first = Start(from, bucket);
        var candidate = Start(to.AddTicks(-1), bucket);
        for (var count = 1; count < maxBuckets && candidate > first; count++)
        {
            candidate = Start(candidate.AddTicks(-1), bucket);
        }

        return candidate;
    }

    private static (HistoryCalendarUnit Unit, TimeZoneInfo TimeZone) GetCalendar(HistoryBucket bucket) =>
        bucket is { CalendarUnit: { } unit, TimeZone: { } timeZone }
            ? (unit, timeZone)
            : throw new ArgumentException("The bucket has neither a size nor a calendar unit.", nameof(bucket));

    private static DateTime PeriodStartDate(DateTime localTime, HistoryCalendarUnit unit)
    {
        var date = localTime.Date;
        return unit switch
        {
            HistoryCalendarUnit.Day => date,
            HistoryCalendarUnit.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
            HistoryCalendarUnit.Month => new DateTime(date.Year, date.Month, 1),
            _ => new DateTime(date.Year, 1, 1)
        };
    }

    // Local midnight to a UTC instant. A skipped midnight resolves to the transition, the first local time that
    // exists; a repeated one to the earlier instant.
    private static DateTimeOffset ToInstant(DateTime localMidnight, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            var offsetBefore = timeZone.GetUtcOffset(local.AddDays(-1));
            return new DateTimeOffset(local - offsetBefore, TimeSpan.Zero);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Bucket alignment. Fixed buckets are epoch-anchored, matching Postgres time_bucket; calendar buckets start at
/// local period starts in a time zone. All backends produce buckets at identical timestamps for the same bucket and
/// sample timestamps, so the merger never interleaves duplicates.
/// </summary>
public static class BucketAlignment
{
    private static readonly TimeSpan SearchMargin = TimeSpan.FromHours(15);
    private static readonly TimeSpan TransitionScanStep = TimeSpan.FromHours(6);

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
    /// anchored at the Unix epoch. A calendar period starts at the earliest instant whose local date in the bucket's
    /// time zone is on or after the period's first date: local midnight, or the transition when the clocks skip it,
    /// or the earlier instant when it occurs twice. Buckets partition time by these start instants, so the result is
    /// at or before <paramref name="timestamp"/> and the next bucket starts after it.
    /// </summary>
    public static DateTimeOffset Start(DateTimeOffset timestamp, HistoryBucket bucket)
    {
        if (bucket.FixedSize is { } size)
        {
            return BucketStart(timestamp, size);
        }

        var (unit, timeZone) = GetCalendar(bucket);
        var periodStart = PeriodStartDate(TimeZoneInfo.ConvertTime(timestamp, timeZone).DateTime, unit);
        var start = LocalTimeToInstant(periodStart, timeZone);

        // The local date can fall back across midnight, so a later period may already have started.
        while (true)
        {
            periodStart = AddPeriod(periodStart, unit);
            var nextStart = LocalTimeToInstant(periodStart, timeZone);
            if (nextStart > timestamp)
            {
                return start;
            }

            start = nextStart;
        }
    }

    /// <summary>
    /// Returns the start of the bucket after the one containing <paramref name="bucketStart"/>, as a UTC instant.
    /// The result is always after <paramref name="bucketStart"/>; a calendar period that never occurs locally, such
    /// as a skipped day, gets no bucket.
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
        while (true)
        {
            periodStart = AddPeriod(periodStart, unit);
            var nextStart = LocalTimeToInstant(periodStart, timeZone);
            if (nextStart > bucketStart)
            {
                return nextStart;
            }
        }
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

    private static DateTime AddPeriod(DateTime periodStart, HistoryCalendarUnit unit) => unit switch
    {
        HistoryCalendarUnit.Day => periodStart.AddDays(1),
        HistoryCalendarUnit.Week => periodStart.AddDays(7),
        HistoryCalendarUnit.Month => periodStart.AddMonths(1),
        _ => periodStart.AddYears(1)
    };

    /// <summary>
    /// Returns the earliest instant, in UTC, whose local time in <paramref name="timeZone"/> is at or after
    /// <paramref name="localTime"/>: the instant itself for an ordinary local time, the instant after the gap for one
    /// the clocks skip, and the earlier instant for one that occurs twice. The kind of <paramref name="localTime"/> is
    /// ignored.
    /// </summary>
    public static DateTimeOffset LocalTimeToInstant(DateTime localTime, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        // Only UTC to local conversions are used, because .NET answers the local to UTC questions (IsInvalidTime,
        // GetUtcOffset(DateTime)) inconsistently with them around some transitions, which made a bucket end where
        // it started.
        var target = new DateTimeOffset(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), TimeSpan.Zero);

        // UTC offsets stay within 14 hours, so the local time is still before localStart here.
        var segmentStart = target - SearchMargin;
        var offset = timeZone.GetUtcOffset(segmentStart);
        while (true)
        {
            // Within a segment of constant offset the local time rises with the instant.
            var candidate = target - offset;
            if (candidate <= segmentStart)
            {
                return segmentStart;
            }

            var offsetChange = FindOffsetChange(timeZone, segmentStart, candidate, offset);
            if (offsetChange is not { } transition)
            {
                return candidate;
            }

            segmentStart = transition;
            offset = timeZone.GetUtcOffset(transition);
        }
    }

    // The first instant in (low, high] whose offset differs from the offset at low, or null. Offset changes
    // that revert within one scan step are not seen; no zone has them.
    private static DateTimeOffset? FindOffsetChange(TimeZoneInfo timeZone, DateTimeOffset low, DateTimeOffset high, TimeSpan offset)
    {
        while (low < high)
        {
            var probe = high - low > TransitionScanStep ? low + TransitionScanStep : high;
            if (timeZone.GetUtcOffset(probe) != offset)
            {
                while ((probe - low).Ticks > 1)
                {
                    var middle = low.AddTicks((probe - low).Ticks / 2);
                    if (timeZone.GetUtcOffset(middle) == offset)
                    {
                        low = middle;
                    }
                    else
                    {
                        probe = middle;
                    }
                }

                return probe;
            }

            low = probe;
        }

        return null;
    }
}

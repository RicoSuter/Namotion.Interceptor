namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Epoch-anchored bucket alignment. All backends must produce buckets at identical timestamps
/// for the same (bucket size, sample timestamps), matching Postgres time_bucket, so the merger
/// never interleaves duplicates.
/// </summary>
public static class BucketAlignment
{
    /// <summary>
    /// Returns the start of the bucket containing <paramref name="ts"/> for the given
    /// <paramref name="bucket"/> size, anchored at the Unix epoch.
    /// </summary>
    public static DateTimeOffset BucketStart(DateTimeOffset ts, TimeSpan bucket)
    {
        var ticksFromEpoch = (ts - DateTimeOffset.UnixEpoch).Ticks;
        return DateTimeOffset.UnixEpoch.AddTicks((ticksFromEpoch / bucket.Ticks) * bucket.Ticks);
    }
}

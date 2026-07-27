using System.Globalization;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// The time span a single partition file covers. Public because it is the type of the
/// partition interval accepted by <see cref="SqliteHistoryStore"/> and exposed as a configuration
/// property by its subject adapter.
/// </summary>
public enum PartitionInterval
{
    Daily,
    Weekly,
    Monthly
}

/// <summary>
/// Maps timestamps to stable partition-file keys and back to their half-open time ranges.
/// </summary>
internal static class SqlitePartition
{
    // Stable file-name key for the partition containing the timestamp.
    public static string PartitionKey(DateTimeOffset timestamp, PartitionInterval interval)
    {
        var utc = timestamp.ToUniversalTime();
        return interval switch
        {
            PartitionInterval.Daily => $"{utc:yyyy-MM-dd}",
            PartitionInterval.Monthly => $"{utc:yyyy-MM}",
            _ => WeeklyKey(utc) // Weekly: ISO-week-anchored on Monday
        };
    }

    // Returns true when the key is a valid partition key under any interval. Used to filter out
    // non-partition database files in the same directory (for example the metadata database).
    //
    // Deliberately interval-independent. Matching only the configured interval's shape made every file
    // written under a previous interval invisible: unreadable by queries and, worse, skipped by the
    // sweep, so it was never deleted and its data stayed inside a coverage claim it could not serve.
    public static bool IsPartitionKey(string key) => TryInferRange(key, out _);

    // The half-open range a key covers, inferred from the key's own shape rather than the configured
    // interval, so a directory holding files from more than one interval still resolves correctly.
    //
    // "yyyy-MM" is a month. "yyyy-MM-dd" is a single day under Daily and a Monday-anchored week under
    // Weekly; a date that is not a Monday can only be Daily, and a Monday is genuinely ambiguous, so
    // the wider reading (a week) is assumed there. That errs safely in both directions: a read opens a
    // file whose rows the SQL range then filters out anyway, and a sweep holds a partition a few days
    // past its retention instead of deleting data that is still inside a coverage claim.
    public static (DateTimeOffset Start, DateTimeOffset End) InferredRange(string key)
    {
        if (!TryInferRange(key, out var range))
        {
            throw new ArgumentException($"'{key}' is not a partition key.", nameof(key));
        }

        return range;
    }

    private static bool TryInferRange(string key, out (DateTimeOffset Start, DateTimeOffset End) range)
    {
        if (DateTimeOffset.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day))
        {
            range = (day, day.AddDays(day.DayOfWeek == DayOfWeek.Monday ? 7 : 1));
            return true;
        }

        if (DateTimeOffset.TryParseExact(key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var month))
        {
            range = (month, month.AddMonths(1));
            return true;
        }

        range = default;
        return false;
    }

    private static string WeeklyKey(DateTimeOffset utc)
    {
        var date = utc.Date;
        var deltaToMonday = ((int)date.DayOfWeek + 6) % 7; // Monday=0
        var monday = date.AddDays(-deltaToMonday);
        return $"{monday:yyyy-MM-dd}";
    }
}

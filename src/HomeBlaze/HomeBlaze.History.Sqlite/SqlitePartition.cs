using System.Globalization;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// The time span a single partition file covers. Public because it is the type of the
/// <see cref="SqliteHistoryStore.PartitionInterval"/> configuration property.
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

    // [start, end) of the partition with this key.
    public static (DateTimeOffset Start, DateTimeOffset End) PartitionRange(string key, PartitionInterval interval)
    {
        switch (interval)
        {
            case PartitionInterval.Daily:
            {
                var start = DateTimeOffset.ParseExact(key, "yyyy-MM-dd", null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                return (start, start.AddDays(1));
            }
            case PartitionInterval.Monthly:
            {
                var start = DateTimeOffset.ParseExact(key + "-01", "yyyy-MM-dd", null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                return (start, start.AddMonths(1));
            }
            default:
            {
                // key is the Monday (yyyy-MM-dd) of the ISO week start.
                var start = DateTimeOffset.ParseExact(key, "yyyy-MM-dd", null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                return (start, start.AddDays(7));
            }
        }
    }

    // All partition keys whose ranges overlap [from, to), in ascending order.
    public static IEnumerable<string> EnumeratePartitionKeys(DateTimeOffset from, DateTimeOffset to, PartitionInterval interval)
    {
        var cursorKey = PartitionKey(from, interval);
        var guard = 0;
        while (true)
        {
            yield return cursorKey;
            var (_, end) = PartitionRange(cursorKey, interval);
            if (end >= to || ++guard > 100_000)
            {
                yield break;
            }

            cursorKey = PartitionKey(end, interval); // next partition starts at this end
        }
    }

    // Returns true when the key is a valid partition key for the interval. Used to filter out
    // non-partition database files in the same directory (for example the moves database).
    public static bool IsPartitionKey(string key, PartitionInterval interval)
    {
        var candidate = interval == PartitionInterval.Monthly ? key + "-01" : key;
        return DateTimeOffset.TryParseExact(candidate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
    }

    private static string WeeklyKey(DateTimeOffset utc)
    {
        var date = utc.Date;
        var deltaToMonday = ((int)date.DayOfWeek + 6) % 7; // Monday=0
        var monday = date.AddDays(-deltaToMonday);
        return $"{monday:yyyy-MM-dd}";
    }
}

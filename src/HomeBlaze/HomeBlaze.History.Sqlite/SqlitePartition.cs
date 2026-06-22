using System.Globalization;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// The time span a single partition file covers.
/// </summary>
internal enum PartitionInterval
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

    private static string WeeklyKey(DateTimeOffset utc)
    {
        var date = utc.Date;
        var deltaToMonday = ((int)date.DayOfWeek + 6) % 7; // Monday=0
        var monday = date.AddDays(-deltaToMonday);
        return $"{monday:yyyy-MM-dd}";
    }
}

using System.Text.Json;

namespace HomeBlaze.History.InMemory;

/// <summary>
/// One typed in-memory sample: the timestamp plus exactly one populated value column
/// (or all-null for an explicitly recorded null). Mirrors the value_long / value_double /
/// value_json column triple used by the persistent stores so query results stay identical.
/// </summary>
internal readonly struct Sample(DateTimeOffset timestamp, long? longValue, double? doubleValue, JsonElement? json)
{
    public DateTimeOffset Timestamp { get; } = timestamp;
    public long? Long { get; } = longValue;
    public double? Double { get; } = doubleValue;
    public JsonElement? Json { get; } = json;

    /// <summary>True when every value column is null (an explicitly recorded null value).</summary>
    public bool IsNull => Long is null && Double is null && Json is null;
}

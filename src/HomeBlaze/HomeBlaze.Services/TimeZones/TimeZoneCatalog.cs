namespace HomeBlaze.Services;

/// <summary>A selectable timezone entry: an IANA id and a human label with its base offset.</summary>
public readonly record struct TimeZoneOption(string Id, string DisplayName);

/// <summary>
/// Builds the list of selectable timezones from the system zone database, presented with IANA ids.
/// </summary>
public static class TimeZoneCatalog
{
    /// <summary>
    /// Returns the system timezones mapped to IANA ids, deduplicated and sorted by id. The base UTC
    /// offset in the label is a hint for the picker and ignores daylight saving.
    /// </summary>
    public static IReadOnlyList<TimeZoneOption> GetZones()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<TimeZoneOption>();

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            var ianaId = ToIanaId(zone.Id);
            if (seen.Add(ianaId))
            {
                options.Add(new TimeZoneOption(ianaId, $"{ianaId} ({FormatOffset(zone.BaseUtcOffset)})"));
            }
        }

        options.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return options;
    }

    private static string ToIanaId(string systemId) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(systemId, out var ianaId) && ianaId is not null
            ? ianaId
            : systemId;

    private static string FormatOffset(TimeSpan offset) =>
        offset == TimeSpan.Zero
            ? "UTC"
            : $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
}

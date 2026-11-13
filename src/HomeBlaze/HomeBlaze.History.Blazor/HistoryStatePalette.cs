namespace HomeBlaze.History.Blazor;

/// <summary>
/// Gives each state label a color in first-seen order, with the common boolean pair pinned so "Yes" is
/// always the highlighted state rather than whichever appeared first. One instance per chart, so a label
/// has one color in every band of that chart. Not thread-safe.
/// </summary>
internal sealed class HistoryStatePalette
{
    private const string PinnedOffColor = "#9E9E9E";

    // The first line color is pinned to the on state, so the other states take the line colors after it.
    private static readonly string PinnedOnColor = HistoryChartPalette.LineColors[0];
    private static readonly string[] Colors = HistoryChartPalette.LineColors.Skip(1).ToArray();

    private readonly Dictionary<string, string> _colors = new(StringComparer.Ordinal);
    private readonly List<HistoryChartLegendEntry> _assigned = [];
    private int _cursor;

    /// <summary>Gets the labels colored so far with their colors, in first-seen order.</summary>
    public IReadOnlyList<HistoryChartLegendEntry> Assigned => _assigned;

    /// <summary>Gets the color of a label, assigning the next one on first use.</summary>
    /// <param name="label">The state label.</param>
    /// <returns>The CSS color of the label.</returns>
    public string GetColor(string label)
    {
        if (_colors.TryGetValue(label, out var existing))
        {
            return existing;
        }

        // Only a label that takes a palette slot moves the cursor, so a pinned label does not push a
        // later label onto a color already in use.
        var color = label switch
        {
            "Yes" or "True" or "On" => PinnedOnColor,
            "No" or "False" or "Off" => PinnedOffColor,
            _ => Colors[_cursor++ % Colors.Length]
        };

        _colors[label] = color;
        _assigned.Add(new HistoryChartLegendEntry(label, color));
        return color;
    }
}

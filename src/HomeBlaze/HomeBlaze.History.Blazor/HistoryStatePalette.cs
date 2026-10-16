namespace HomeBlaze.History.Blazor;

/// <summary>
/// Gives each state label a colour in first-seen order, with the common boolean pair pinned so "Yes" is
/// always the highlighted state rather than whichever appeared first. One instance per chart, so a label
/// has one colour in every band of that chart. Not thread-safe.
/// </summary>
internal sealed class HistoryStatePalette
{
    private const string PinnedOnColor = "#2979FF";
    private const string PinnedOffColor = "#9E9E9E";

    private static readonly string[] Colors = ["#00B8A9", "#F6A609", "#8E44AD", "#E4572E", "#3D5A80", "#C2185B"];

    private readonly Dictionary<string, string> _colors = new(StringComparer.Ordinal);
    private readonly List<HistoryChartLegendEntry> _assigned = [];
    private int _cursor;

    /// <summary>Gets the labels coloured so far with their colours, in first-seen order.</summary>
    public IReadOnlyList<HistoryChartLegendEntry> Assigned => _assigned;

    /// <summary>Gets the colour of a label, assigning the next one on first use.</summary>
    /// <param name="label">The state label.</param>
    /// <returns>The CSS colour of the label.</returns>
    public string GetColor(string label)
    {
        if (_colors.TryGetValue(label, out var existing))
        {
            return existing;
        }

        // Only a label that takes a palette slot moves the cursor, so a pinned label does not push a
        // later label onto a colour already in use.
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

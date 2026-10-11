using HomeBlaze.AI.Agents;

namespace HomeBlaze.AI.Blazor;

/// <summary>
/// A row of the chat timeline: one entry, or consecutive quiet background runs collapsed into one row.
/// </summary>
/// <param name="FirstIndex">The index of the oldest entry of the row.</param>
/// <param name="LastIndex">The index of the newest entry of the row, the one the row shows.</param>
public readonly record struct AgentTimelineRow(int FirstIndex, int LastIndex)
{
    /// <summary>How many entries the row stands for.</summary>
    public int EntryCount => LastIndex - FirstIndex + 1;
}

/// <summary>
/// Limits the chat to the newest rows of the timeline.
/// </summary>
public static class AgentTimelineRows
{
    /// <summary>How many rows the chat shows at first and adds per "Show earlier".</summary>
    public const int PageSize = 50;

    /// <summary>
    /// Fills <paramref name="rows"/>, oldest first, with the newest <paramref name="maximumRowCount"/> rows of
    /// <paramref name="entries"/>. Consecutive quiet background runs form one row, which is never split.
    /// </summary>
    /// <returns>Whether earlier rows exist.</returns>
    public static bool GetNewest(IReadOnlyList<AgentTimelineEntry> entries, int maximumRowCount, List<AgentTimelineRow> rows)
    {
        rows.Clear();
        var index = entries.Count - 1;
        while (index >= 0 && rows.Count < maximumRowCount)
        {
            var lastIndex = index;
            if (IsQuietRun(entries[index]))
            {
                while (index > 0 && IsQuietRun(entries[index - 1]))
                {
                    index--;
                }
            }

            rows.Add(new AgentTimelineRow(index, lastIndex));
            index--;
        }

        rows.Reverse();
        return index >= 0;
    }

    private static bool IsQuietRun(AgentTimelineEntry entry) =>
        entry is { Kind: AgentTimelineEntryKind.BackgroundRun, IsQuiet: true };
}

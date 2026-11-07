using System.Text;

namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>How the editor shows and keeps the event types of a trigger.</summary>
internal static class AutomationEventNames
{
    private const string Suffix = "Event";

    /// <summary>The name of an event type for display: without the Event suffix and with its words split, as in "Automation state changed".</summary>
    public static string Humanize(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return string.Empty;
        }

        var name = typeName.Length > Suffix.Length && typeName.EndsWith(Suffix, StringComparison.Ordinal) ? typeName[..^Suffix.Length] : typeName;
        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var isWordStart = index > 0 && char.IsUpper(name[index]) && !char.IsUpper(name[index - 1]);
            builder.Append(isWordStart ? $" {char.ToLowerInvariant(name[index])}" : name[index].ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// The event type after the source changed: the one the user chose when the source publishes it, otherwise the current
    /// one while the source is unknown or still publishes it, otherwise the first one of the source. A source that is being
    /// typed passes through other subjects, which must not replace the chosen event type.
    /// </summary>
    public static string Choose(string chosen, string current, IReadOnlyList<string> available)
    {
        if (available.Contains(chosen))
        {
            return chosen;
        }

        return available.Count == 0 || available.Contains(current) ? current : available[0];
    }
}

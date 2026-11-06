using System.Text.RegularExpressions;

namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>Reads the text of a duration field: one duration literal, such as <c>20s</c> or <c>1h30m</c>, and nothing else.</summary>
internal static partial class DurationInput
{
    [GeneratedRegex(@"^(\d+(ms|d|h|m|s))+$", RegexOptions.CultureInvariant)]
    private static partial Regex Literal();

    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        return text is not null && Literal().IsMatch(text.Trim()) && AutomationDuration.TryParse(text, out duration);
    }
}

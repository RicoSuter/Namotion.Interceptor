using System.Globalization;

namespace HomeBlaze.AI.Blazor;

/// <summary>
/// Formats and parses the time span fields of the agent edit form.
/// </summary>
public static class AgentTimeSpanInput
{
    /// <summary>The largest accepted time span.</summary>
    public static readonly TimeSpan Maximum = TimeSpan.FromDays(365);

    /// <summary>Formats a time span as [d.]hh:mm:ss.</summary>
    public static string Format(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses [d.]hh:mm[:ss] into a time span between zero and <see cref="Maximum"/>. A bare number is rejected
    /// because it would parse as days.
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(':') ||
            !TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out value) ||
            value < TimeSpan.Zero || value > Maximum)
        {
            value = TimeSpan.Zero;
            return false;
        }

        return true;
    }
}

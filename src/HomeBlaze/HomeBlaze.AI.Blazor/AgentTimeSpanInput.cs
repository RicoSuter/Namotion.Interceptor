using System.Globalization;

namespace HomeBlaze.AI.Blazor;

/// <summary>
/// Formats and parses the time span fields of the agent edit form.
/// </summary>
public static class AgentTimeSpanInput
{
    /// <summary>The largest accepted time span.</summary>
    public static readonly TimeSpan Maximum = TimeSpan.FromDays(365);

    /// <summary>The largest turn timeout the runtime enforces; a longer one applies no limit.</summary>
    public static readonly TimeSpan MaximumTurnTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

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

    /// <summary>
    /// Parses a turn timeout like <see cref="TryParse"/>, but at most <see cref="MaximumTurnTimeout"/>. Zero means
    /// no limit.
    /// </summary>
    public static bool TryParseTurnTimeout(string? text, out TimeSpan value)
    {
        if (!TryParse(text, out value) || value > MaximumTurnTimeout)
        {
            value = TimeSpan.Zero;
            return false;
        }

        return true;
    }
}

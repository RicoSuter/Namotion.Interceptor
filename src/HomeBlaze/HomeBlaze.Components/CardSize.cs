using System.Globalization;

namespace HomeBlaze.Components;

/// <summary>
/// Turns the <see cref="Card.Height"/> and <see cref="Card.AspectRatio"/> of a card into its size.
/// </summary>
public static class CardSize
{
    /// <summary>
    /// Parses an aspect ratio written as width and height with a colon between them, for example <c>4:3</c>.
    /// Both numbers are invariant, finite and above zero; white space around them is allowed.
    /// </summary>
    public static bool TryParseAspectRatio(string? value, out double width, out double height)
    {
        width = 0;
        height = 0;

        var text = value.AsSpan();
        var colon = text.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        return TryParsePositive(text[..colon], out width) &&
               TryParsePositive(text[(colon + 1)..], out height);
    }

    /// <summary>
    /// Gets the inline style that sizes a card, or null when the card takes the height of its content.
    /// <paramref name="height"/> wins over <paramref name="aspectRatio"/>; an invalid value of either is an
    /// error, also an aspect ratio that the height overrides.
    /// </summary>
    public static string? GetStyle(int? height, string? aspectRatio, out string? error)
    {
        error = null;

        var hasAspectRatio = !string.IsNullOrWhiteSpace(aspectRatio);
        double ratioWidth = 0, ratioHeight = 0;
        if (hasAspectRatio && !TryParseAspectRatio(aspectRatio, out ratioWidth, out ratioHeight))
        {
            error = $"AspectRatio must look like 4:3: {aspectRatio}";
            return null;
        }

        if (height is { } pixels)
        {
            if (pixels <= 0)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"Height must be above 0: {pixels}");
                return null;
            }

            return string.Create(CultureInfo.InvariantCulture, $"height: {pixels}px");
        }

        return hasAspectRatio
            ? string.Create(CultureInfo.InvariantCulture, $"aspect-ratio: {ratioWidth} / {ratioHeight}")
            : null;
    }

    private static bool TryParsePositive(ReadOnlySpan<char> text, out double number) =>
        double.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) &&
        double.IsFinite(number) &&
        number > 0;
}

using System.Globalization;

namespace HomeBlaze.Game.View;

internal static class GameColors
{
    /// <summary>
    /// Gets a color that stays the same for the id across sessions and processes.
    /// </summary>
    public static string FromId(string id) => FromHsl(Hash(id) % 360, 0.65, 0.55);

    /// <summary>
    /// Gets the stored color in lower case when it is a <c>#rrggbb</c> color, else the color of the id.
    /// </summary>
    public static string Resolve(string? stored, string id) =>
        stored is { Length: 7 } && stored[0] == '#' && !stored.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")
            ? stored.ToLowerInvariant()
            : FromId(id);

    /// <summary>
    /// Gets a hash that stays the same across sessions and processes.
    /// </summary>
    internal static uint Hash(string value)
    {
        // FNV-1a, because string.GetHashCode differs per process.
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return hash;
    }

    private static string FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var segment = hue / 60;
        var second = chroma * (1 - Math.Abs(segment % 2 - 1));
        var (red, green, blue) = segment switch
        {
            < 1 => (chroma, second, 0d),
            < 2 => (second, chroma, 0d),
            < 3 => (0d, chroma, second),
            < 4 => (0d, second, chroma),
            < 5 => (second, 0d, chroma),
            _ => (chroma, 0d, second)
        };
        var match = lightness - chroma / 2;
        return string.Create(CultureInfo.InvariantCulture,
            $"#{ToByte(red + match):x2}{ToByte(green + match):x2}{ToByte(blue + match):x2}");
    }

    private static int ToByte(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
}

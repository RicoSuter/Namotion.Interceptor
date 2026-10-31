using System.Globalization;

namespace HomeBlaze.Game.View;

internal static class GameColors
{
    public const string Wall = "#8d8577";
    public const string Door = "#8b5a2b";

    /// <summary>
    /// Gets a color that stays the same for the id across sessions and processes.
    /// </summary>
    public static string FromId(string id)
    {
        // FNV-1a, because string.GetHashCode differs per process.
        var hash = 2166136261u;
        foreach (var character in id)
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return FromHsl(hash % 360, 0.65, 0.55);
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

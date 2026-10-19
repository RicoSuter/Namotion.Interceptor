using System.Globalization;
using System.Text.RegularExpressions;
using MudBlazor;

namespace HomeBlaze.Components;

/// <summary>
/// The color of a value: a theme color, or a CSS color. A widget passes <see cref="Theme"/> to MudBlazor and
/// <see cref="Style"/> as the inline style of the same element. Equal colors from the same case of a map are the same
/// instance.
/// </summary>
public sealed partial class ValueColor
{
    private const string DarkText = "rgba(0, 0, 0, 0.87)";
    private const string LightText = "#ffffff";
    private const string ThemeText = "var(--mud-palette-text-primary)";

    // One instance per theme color, so that converting a theme color allocates nothing.
    private static readonly ValueColor[] ThemeColors = Enum.GetValues<Color>()
        .Select(color => new ValueColor(color))
        .ToArray();

    private static readonly string[] ThemeNames = ThemeColors
        .Select(color => color.Theme.ToString())
        .ToArray();

    private ValueColor(Color theme)
    {
        Theme = theme;
    }

    private ValueColor(string css, string contrastText)
    {
        // The primary color of MudBlazor, overridden on the element, so that every MudBlazor component colors and fills
        // with the CSS color through its own primary classes. The hover color is the 6 percent of the theme.
        Theme = Color.Primary;
        Css = css;
        ContrastText = contrastText;
        Style = $"--mud-palette-primary: {css}; --mud-palette-primary-text: {contrastText}; --mud-palette-primary-hover: color-mix(in srgb, {css} 6%, transparent)";
    }

    /// <summary>The theme color <see cref="Color.Default"/>.</summary>
    public static ValueColor Default { get; } = ThemeColors[(int)Color.Default];

    /// <summary>The theme color to pass to MudBlazor. For a CSS color it is <see cref="Color.Primary"/>, which <see cref="Style"/> redefines.</summary>
    public Color Theme { get; }

    /// <summary>The CSS color as written, or null for a theme color.</summary>
    public string? Css { get; }

    /// <summary>Whether this is the theme color <see cref="Color.Default"/>.</summary>
    public bool IsDefault => Css is null && Theme == Color.Default;

    /// <summary>
    /// The inline style that redefines the primary color of MudBlazor as the CSS color, and the text on it as black or
    /// white, whichever contrasts more, or the text color of the theme for a <c>var()</c>. Null for a theme color.
    /// </summary>
    public string? Style { get; }

    /// <summary>The text color on a fill of the CSS color, or null for a theme color.</summary>
    internal string? ContrastText { get; }

    /// <summary>The tile styles of the CSS color by variant, built by <see cref="Controls.TileStyle"/> on first use.</summary>
    internal string[]? TileStyles { get; set; }

    /// <summary>Gets the color of a theme color. A value that <see cref="Color"/> does not define is <see cref="Default"/>.</summary>
    internal static ValueColor FromTheme(Color color) =>
        (uint)color < (uint)ThemeColors.Length ? ThemeColors[(int)color] : Default;

    public static implicit operator ValueColor(Color color) => FromTheme(color);

    /// <summary>
    /// Parses the name of a theme color, without regard to case, or a CSS color: <c>#rgb</c>, <c>#rgba</c>,
    /// <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>rgb()</c> or <c>rgba()</c> with numbers or percentages, or
    /// <c>var(--mud-palette-name)</c>. Null, empty and white space give <see cref="Default"/>. Anything else,
    /// a number for a theme color included, is no color.
    /// </summary>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ValueColor? color)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty)
        {
            color = Default;
            return true;
        }

        for (var index = 0; index < ThemeNames.Length; index++)
        {
            if (trimmed.Equals(ThemeNames[index], StringComparison.OrdinalIgnoreCase))
            {
                color = ThemeColors[index];
                return true;
            }
        }

        var css = trimmed.ToString();
        if (HexColorRegex().IsMatch(css))
        {
            color = new ValueColor(css, GetContrastText(ParseHex(css)));
            return true;
        }

        var rgb = RgbColorRegex().Match(css);
        if (rgb.Success)
        {
            var channels = (
                ParseChannel(rgb.Groups["red"].Value),
                ParseChannel(rgb.Groups["green"].Value),
                ParseChannel(rgb.Groups["blue"].Value));
            color = new ValueColor(css, GetContrastText(channels));
            return true;
        }

        if (PaletteVariableRegex().IsMatch(css))
        {
            color = new ValueColor(css, ThemeText);
            return true;
        }

        color = null;
        return false;
    }

    /// <summary>Gets the widget argument error of a color that <see cref="TryParse"/> does not take.</summary>
    internal static string GetError(string? text) =>
        $"Unknown color '{text}': use a theme color like Success or a CSS color like #ffffff80";

    public override string ToString() => Css ?? Theme.ToString();

    private static (double Red, double Green, double Blue) ParseHex(string css)
    {
        // #rgb and #rgba repeat each digit, #rrggbb and #rrggbbaa do not. The alpha is left out.
        var digits = css.AsSpan(1);
        if (digits.Length <= 4)
        {
            return (ParseHexDigits(digits[0], digits[0]), ParseHexDigits(digits[1], digits[1]), ParseHexDigits(digits[2], digits[2]));
        }

        return (ParseHexDigits(digits[0], digits[1]), ParseHexDigits(digits[2], digits[3]), ParseHexDigits(digits[4], digits[5]));
    }

    private static double ParseHexDigits(char high, char low) =>
        HexToInt(high) * 16 + HexToInt(low);

    private static int HexToInt(char digit) =>
        digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10;

    private static double ParseChannel(string channel)
    {
        var isPercent = channel.EndsWith('%');
        var number = double.Parse(isPercent ? channel.AsSpan(0, channel.Length - 1) : channel, NumberStyles.Float, CultureInfo.InvariantCulture);
        return Math.Clamp(isPercent ? number * 2.55 : number, 0, 255);
    }

    // The text on a fill is black or white, whichever has the larger contrast ratio to the fill (WCAG), with the alpha
    // of the fill left out, because what shines through is not known.
    private static string GetContrastText((double Red, double Green, double Blue) channels)
    {
        var luminance = 0.2126 * Linearize(channels.Red) + 0.7152 * Linearize(channels.Green) + 0.0722 * Linearize(channels.Blue);
        return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? DarkText : LightText;
    }

    private static double Linearize(double channel)
    {
        var value = channel / 255;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();

    // CSS white space only, and numbers without signs or exponents, as CSS takes them.
    private const string Space = "[ \\t\\r\\n\\f]";
    private const string Number = "[0-9]+(?:\\.[0-9]+)?";
    private const string Alpha = "(?:[0-9]+(?:\\.[0-9]+)?|\\.[0-9]+)%?";

    // The comma syntax, with all channels numbers or all percentages, and the space syntax with a slash before the alpha,
    // which may mix them. Only digits, dots, percent signs, commas, slashes and white space stand between the parentheses,
    // so the text cannot end the declaration of the style.
    [GeneratedRegex(
        "^rgba?\\(" + Space + "*(?:" +
        "(?<red>" + Number + ")" + Space + "*," + Space + "*(?<green>" + Number + ")" + Space + "*," + Space + "*(?<blue>" + Number + ")(?:" + Space + "*," + Space + "*" + Alpha + ")?" +
        "|(?<red>" + Number + "%)" + Space + "*," + Space + "*(?<green>" + Number + "%)" + Space + "*," + Space + "*(?<blue>" + Number + "%)(?:" + Space + "*," + Space + "*" + Alpha + ")?" +
        "|(?<red>" + Number + "%?)" + Space + "+(?<green>" + Number + "%?)" + Space + "+(?<blue>" + Number + "%?)(?:" + Space + "*/" + Space + "*" + Alpha + ")?" +
        ")" + Space + "*\\)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RgbColorRegex();

    [GeneratedRegex("^var\\(" + Space + "*--mud-palette-[a-z0-9]+(?:-[a-z0-9]+)*" + Space + "*\\)$", RegexOptions.CultureInvariant)]
    private static partial Regex PaletteVariableRegex();
}

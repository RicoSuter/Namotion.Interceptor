using System.Globalization;
using System.Text.RegularExpressions;
using MudBlazor;

namespace HomeBlaze.Components;

/// <summary>
/// The color of a value: a theme color, or a CSS color that a widget applies as an inline style. Equal colors from the
/// same case of a map are the same instance.
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

    private ValueColor(string css, string fillText)
    {
        Theme = Color.Default;
        Css = css;
        TextStyle = $"color: {css}";
        FillStyle = $"background-color: {css}; color: {fillText}";
        TileFilledStyle = $"background: {css}; color: {fillText}";
        TileOutlinedStyle = $"background: var(--mud-palette-surface); color: {css}; box-shadow: inset 0 0 0 2px {css}";
        TileTextStyle = $"background: var(--mud-palette-surface); color: {css}";
    }

    /// <summary>The theme color <see cref="Color.Default"/>.</summary>
    public static ValueColor Default { get; } = FromTheme(Color.Default);

    /// <summary>The theme color, <see cref="Color.Default"/> for a CSS color.</summary>
    public Color Theme { get; }

    /// <summary>The CSS color as written, or null for a theme color.</summary>
    public string? Css { get; }

    /// <summary>Whether this is the theme color <see cref="Color.Default"/>.</summary>
    public bool IsDefault => Css is null && Theme == Color.Default;

    /// <summary>The inline style that gives text or an icon the CSS color, or null for a theme color.</summary>
    public string? TextStyle { get; }

    /// <summary>
    /// The inline style that fills a chip with the CSS color, with black or white text, whichever contrasts more, or
    /// the text color of the theme for a <c>var()</c>, or null for a theme color.
    /// </summary>
    public string? FillStyle { get; }

    internal string? TileFilledStyle { get; }

    internal string? TileOutlinedStyle { get; }

    internal string? TileTextStyle { get; }

    /// <summary>Gets the color of a theme color.</summary>
    public static ValueColor FromTheme(Color color) =>
        (uint)color < (uint)ThemeColors.Length && ThemeColors[(int)color].Theme == color ? ThemeColors[(int)color] : new ValueColor(color);

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
    public static string GetError(string? text) =>
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

    // The comma syntax, and the space syntax with a slash before the alpha. Only digits, dots, percent signs, commas,
    // slashes and spaces stand between the parentheses, so the text cannot end the declaration of the style.
    [GeneratedRegex(
        @"^rgba?\(\s*(?<red>[0-9]+(?:\.[0-9]+)?%?)(?:\s*,\s*(?<green>[0-9]+(?:\.[0-9]+)?%?)\s*,\s*(?<blue>[0-9]+(?:\.[0-9]+)?%?)(?:\s*,\s*(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)%?)?|\s+(?<green>[0-9]+(?:\.[0-9]+)?%?)\s+(?<blue>[0-9]+(?:\.[0-9]+)?%?)(?:\s*/\s*(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)%?)?)\s*\)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RgbColorRegex();

    [GeneratedRegex(@"^var\(\s*--mud-palette-[a-z0-9]+(?:-[a-z0-9]+)*\s*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex PaletteVariableRegex();
}

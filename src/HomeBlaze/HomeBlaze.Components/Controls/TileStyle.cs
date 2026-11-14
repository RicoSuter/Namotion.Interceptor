using MudBlazor;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// Gets the inline style of a tile: its fill and its text color from a theme color or a CSS color and a variant.
/// </summary>
internal static class TileStyle
{
    private const string Surface = "background: var(--mud-palette-surface); color: var(--mud-palette-text-primary)";

    // Built once, so that a render allocates no style text. Indexed by color, then by variant.
    private static readonly string[,] Styles = BuildStyles();

    /// <summary>
    /// Gets the style of a tile. A color without a palette fill (Default, Inherit, Transparent, Surface) draws the tile
    /// on the surface, as does a color or a variant that is not defined (a configuration can give any integer). A
    /// disabled tile keeps its style and is dimmed by its class instead. A CSS color fills the tile like a theme color,
    /// with the text color of <see cref="ValueColor.ContrastText"/>. A text color that is not <see cref="ValueColor.Default"/>
    /// replaces the text color of every defined variant, also on the surface, and an outline keeps the color.
    /// </summary>
    public static string GetStyle(ValueColor color, Variant variant, ValueColor? textColor = null)
    {
        // The unsigned comparison also rejects a negative value.
        if ((uint)variant >= (uint)Styles.GetLength(1))
        {
            return Surface;
        }

        if (textColor is { IsDefault: false })
        {
            // Built once per text color and fill and kept with them.
            var textOnFill = ValueColor.GetTextOnFill(color, textColor);
            var textStyles = textOnFill.TileStyles ??= BuildStyles(GetFill(color), textOnFill.TextCss, textOnFill.TextCss);
            return textStyles[(int)variant];
        }

        if (color.Css is { } css)
        {
            // Built once per CSS color and kept on it. A tile does not take the palette override of ValueColor.Style,
            // because its badge would inherit it.
            var cssStyles = color.TileStyles ??= BuildStyles(css, color.ContrastText!, css);
            return cssStyles[(int)variant];
        }

        return (uint)color.Theme < (uint)Styles.GetLength(0) ? Styles[(int)color.Theme, (int)variant] : Surface;
    }

    private static string[,] BuildStyles()
    {
        var colors = Enum.GetValues<Color>();
        var variants = Enum.GetValues<Variant>();
        var styles = new string[colors.Max(color => (int)color) + 1, variants.Max(variant => (int)variant) + 1];
        foreach (var color in colors)
        {
            var name = ValueColor.GetPaletteName(color);
            foreach (var variant in variants)
            {
                styles[(int)color, (int)variant] = name is null
                    ? Surface
                    : BuildStyle($"var(--mud-palette-{name})", $"var(--mud-palette-{name}-text)", $"var(--mud-palette-{name})", variant);
            }
        }

        return styles;
    }

    // A null fill draws every variant on the surface.
    private static string[] BuildStyles(string? fill, string fillText, string surfaceText)
    {
        var styles = new string[Styles.GetLength(1)];
        for (var variant = 0; variant < styles.Length; variant++)
        {
            styles[variant] = fill is null
                ? $"background: var(--mud-palette-surface); color: {surfaceText}"
                : BuildStyle(fill, fillText, surfaceText, (Variant)variant);
        }

        return styles;
    }

    private static string BuildStyle(string fill, string fillText, string surfaceText, Variant variant) => variant switch
    {
        Variant.Outlined => $"background: var(--mud-palette-surface); color: {surfaceText}; box-shadow: inset 0 0 0 2px {fill}",
        Variant.Text => $"background: var(--mud-palette-surface); color: {surfaceText}",
        _ => $"background: {fill}; color: {fillText}"
    };

    // The CSS value that fills a tile of the color, or null for one that draws it on the surface.
    private static string? GetFill(ValueColor color) =>
        color.Css ?? (ValueColor.GetPaletteName(color.Theme) is { } name ? $"var(--mud-palette-{name})" : null);
}

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
    /// with the text color of <see cref="ValueColor.ContrastText"/>.
    /// </summary>
    public static string GetStyle(ValueColor color, Variant variant)
    {
        // The unsigned comparison also rejects a negative value.
        if ((uint)variant >= (uint)Styles.GetLength(1))
        {
            return Surface;
        }

        if (color.Css is { } css)
        {
            // Built once per CSS color and kept on it. A tile does not take the palette override of ValueColor.Style,
            // because its badge would inherit it.
            var cssStyles = color.TileStyles ??= BuildStyles(css, color.ContrastText!);
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
            var name = GetPaletteName(color);
            foreach (var variant in variants)
            {
                styles[(int)color, (int)variant] = name is null
                    ? Surface
                    : BuildStyle($"var(--mud-palette-{name})", $"var(--mud-palette-{name}-text)", variant);
            }
        }

        return styles;
    }

    private static string[] BuildStyles(string fill, string fillText)
    {
        var styles = new string[Styles.GetLength(1)];
        for (var variant = 0; variant < styles.Length; variant++)
        {
            styles[variant] = BuildStyle(fill, fillText, (Variant)variant);
        }

        return styles;
    }

    private static string BuildStyle(string fill, string fillText, Variant variant) => variant switch
    {
        Variant.Outlined => $"background: var(--mud-palette-surface); color: {fill}; box-shadow: inset 0 0 0 2px {fill}",
        Variant.Text => $"background: var(--mud-palette-surface); color: {fill}",
        _ => $"background: {fill}; color: {fillText}"
    };

    private static string? GetPaletteName(Color color) => color switch
    {
        Color.Primary => "primary",
        Color.Secondary => "secondary",
        Color.Tertiary => "tertiary",
        Color.Info => "info",
        Color.Success => "success",
        Color.Warning => "warning",
        Color.Error => "error",
        Color.Dark => "dark",
        _ => null
    };
}

using MudBlazor;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// Gets the inline style of a tile: its fill and its text color from a theme color and a variant.
/// </summary>
internal static class TileStyle
{
    private const string Surface = "background: var(--mud-palette-surface); color: var(--mud-palette-text-primary)";
    private const string Disabled = "background: var(--mud-palette-action-disabled-background); color: var(--mud-palette-action-disabled)";

    // Built once, so that a render allocates no style text. Indexed by color, then by variant.
    private static readonly string[,] Styles = BuildStyles();

    /// <summary>
    /// Gets the style of a tile. A color without a palette fill (Default, Inherit, Transparent, Surface) draws the tile
    /// on the surface, as does a color or a variant that is not defined (a configuration can give any integer); a
    /// disabled tile has the disabled colors of the theme.
    /// </summary>
    public static string GetStyle(Color color, Variant variant, bool disabled)
    {
        if (disabled)
        {
            return Disabled;
        }

        // The unsigned comparison also rejects a negative value.
        return (uint)color < (uint)Styles.GetLength(0) && (uint)variant < (uint)Styles.GetLength(1)
            ? Styles[(int)color, (int)variant] ?? Surface
            : Surface;
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
                styles[(int)color, (int)variant] = name is null ? Surface : variant switch
                {
                    Variant.Outlined => $"background: var(--mud-palette-surface); color: var(--mud-palette-{name}); box-shadow: inset 0 0 0 2px var(--mud-palette-{name})",
                    Variant.Text => $"background: var(--mud-palette-surface); color: var(--mud-palette-{name})",
                    _ => $"background: var(--mud-palette-{name}); color: var(--mud-palette-{name}-text)"
                };
            }
        }

        return styles;
    }

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

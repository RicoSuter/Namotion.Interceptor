using MudBlazor;

namespace HomeBlaze.Components;

/// <summary>
/// A text color on a fill, as <see cref="ValueColor.GetTextOnFill"/> keeps it, with the styles that draw it.
/// </summary>
internal sealed class TextOnFill
{
    public TextOnFill(ValueColor fill, ValueColor text)
    {
        Fill = fill;
        TextCss = text.Css ?? text.Theme switch
        {
            // The Style of a CSS fill redefines the primary variable as the fill, so the primary text color is read from
            // its channel variable, which Style leaves as it is.
            Color.Primary => "rgb(var(--mud-palette-primary-rgb))",
            Color.Surface => "var(--mud-palette-surface)",
            Color.Inherit => "inherit",
            Color.Transparent => "transparent",
            _ => ValueColor.GetPaletteName(text.Theme) is { } name
                ? $"var(--mud-palette-{name})"
                : ValueColor.ThemeText
        };
        Style = fill.Style is { } fillStyle ? $"{fillStyle}; color: {TextCss}" : $"color: {TextCss}";
    }

    /// <summary>The fill the text is on.</summary>
    public ValueColor Fill { get; }

    /// <summary>The CSS value of the text color.</summary>
    public string TextCss { get; }

    /// <summary>
    /// The inline style of a MudBlazor component that the fill colors: the <see cref="ValueColor.Style"/> of the fill,
    /// followed by the text color, which overrides the text color of the classes of MudBlazor.
    /// </summary>
    public string Style { get; }

    /// <summary>The tile styles by variant, built by <see cref="Controls.TileStyle"/> on first use.</summary>
    public string[]? TileStyles { get; set; }
}

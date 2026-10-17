namespace HomeBlaze.Components;

/// <summary>How a <see cref="ValueTile"/> is drawn.</summary>
public enum ValueTileKind
{
    /// <summary>A raised box with the icon and the label above the value, which has the color of the matched case.</summary>
    Box,

    /// <summary>A tile that fills its place with the icon, the value and the label centered, filled by the color of the matched case.</summary>
    Tile
}

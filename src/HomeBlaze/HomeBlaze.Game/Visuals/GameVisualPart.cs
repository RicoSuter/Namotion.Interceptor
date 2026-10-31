namespace HomeBlaze.Game.Visuals;

/// <summary>
/// One box or model of a <see cref="GameVisual"/>. Positions are in tiles relative to the south-west corner of the
/// object's footprint at ground level.
/// </summary>
public sealed record GameVisualPart
{
    public GameVisualShape Shape { get; init; }

    /// <summary>
    /// The south-west bottom corner of the part's box.
    /// </summary>
    public GameVisualVector Offset { get; init; }

    /// <summary>
    /// The width, height and depth of the part's box. A model is scaled uniformly to the largest size whose bounding box
    /// fits in it, with its own origin at the horizontal center of the box and its lowest point at the box's bottom.
    /// </summary>
    public GameVisualVector Size { get; init; }

    /// <summary>
    /// The point around which <see cref="Rotation"/> turns the part.
    /// </summary>
    public GameVisualPoint Pivot { get; init; }

    /// <summary>
    /// The turn around the vertical axis through <see cref="Pivot"/> in degrees, clockwise seen from above (north toward east).
    /// </summary>
    public double Rotation { get; init; }

    /// <summary>
    /// The URL of the texture of a box, starting with "_content/"; see <see cref="GameAssets.Url"/>.
    /// </summary>
    public string? Texture { get; init; }

    /// <summary>
    /// The tiles one repeat of the texture covers, horizontally and vertically.
    /// </summary>
    public double TextureScale { get; init; } = 1;

    /// <summary>
    /// The color of a box as "#rrggbb", which tints its texture; gray for a box without texture and color.
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    /// The URL of the glTF model of a <see cref="GameVisualShape.Model"/> part, starting with "_content/".
    /// </summary>
    public string? Model { get; init; }

    public GameVisualEffect Effect { get; init; }

    /// <summary>
    /// The name of an animation in the model that plays in a loop, or null.
    /// </summary>
    public string? Animation { get; init; }
}

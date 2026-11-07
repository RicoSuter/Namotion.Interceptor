namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// Where an object fits right in front of a player: the south-west corner of its area, its size in tiles along X (width)
/// and Y (depth), and the direction the player faces.
/// </summary>
public readonly record struct GameSpot(int X, int Y, int Width, int Depth, GameDirection Facing)
{
    /// <summary>
    /// The tiles the object covers at this spot.
    /// </summary>
    public GameArea Area => new(X, Y, Width, Depth);
}

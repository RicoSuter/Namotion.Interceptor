namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// One of the 8 directions a player faces and steps in; north is +Y and east is +X.
/// </summary>
public enum GameDirection
{
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,
    NorthWest
}

/// <summary>
/// Steps of a <see cref="GameDirection"/> on the grid.
/// </summary>
public static class GameDirectionExtensions
{
    /// <summary>
    /// The step along X: 1 toward the east, -1 toward the west, else 0.
    /// </summary>
    public static int GetOffsetX(this GameDirection direction) => direction switch
    {
        GameDirection.NorthEast or GameDirection.East or GameDirection.SouthEast => 1,
        GameDirection.SouthWest or GameDirection.West or GameDirection.NorthWest => -1,
        _ => 0
    };

    /// <summary>
    /// The step along Y: 1 toward the north, -1 toward the south, else 0.
    /// </summary>
    public static int GetOffsetY(this GameDirection direction) => direction switch
    {
        GameDirection.NorthWest or GameDirection.North or GameDirection.NorthEast => 1,
        GameDirection.SouthEast or GameDirection.South or GameDirection.SouthWest => -1,
        _ => 0
    };

    /// <summary>
    /// Whether the direction is north-east, south-east, south-west or north-west.
    /// </summary>
    public static bool IsDiagonal(this GameDirection direction) => ((int)direction & 1) == 1;
}

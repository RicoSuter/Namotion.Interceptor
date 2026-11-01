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

public static class GameDirectionExtensions
{
    public static int GetOffsetX(this GameDirection direction) => direction switch
    {
        GameDirection.NorthEast or GameDirection.East or GameDirection.SouthEast => 1,
        GameDirection.SouthWest or GameDirection.West or GameDirection.NorthWest => -1,
        _ => 0
    };

    public static int GetOffsetY(this GameDirection direction) => direction switch
    {
        GameDirection.NorthWest or GameDirection.North or GameDirection.NorthEast => 1,
        GameDirection.SouthEast or GameDirection.South or GameDirection.SouthWest => -1,
        _ => 0
    };

    public static bool IsDiagonal(this GameDirection direction) => ((int)direction & 1) == 1;
}

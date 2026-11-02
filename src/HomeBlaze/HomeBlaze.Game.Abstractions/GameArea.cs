namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// The tiles from <see cref="X"/> to <see cref="Right"/> and from <see cref="Y"/> to <see cref="Top"/>; a tile is 25 cm,
/// X grows to the east and Y to the north. Width and Depth are at least 1 for a meaningful area; a zero or negative size
/// is an empty area, which is not validated.
/// </summary>
public readonly record struct GameArea(int X, int Y, int Width, int Depth)
{
    /// <summary>
    /// The last tile along X, inclusive; a long so that it cannot overflow.
    /// </summary>
    public long Right => (long)X + Width - 1;

    /// <summary>
    /// The last tile along Y, inclusive; a long so that it cannot overflow.
    /// </summary>
    public long Top => (long)Y + Depth - 1;

    /// <summary>
    /// Whether the two areas share at least one tile.
    /// </summary>
    public bool Intersects(GameArea other) =>
        X <= other.Right && other.X <= Right && Y <= other.Top && other.Y <= Top;

    /// <summary>
    /// Whether the tile is part of the area.
    /// </summary>
    public bool Contains(long x, long y) => x >= X && x <= Right && y >= Y && y <= Top;

    /// <summary>
    /// Moves the area by the offset; returns false when a tile of the result would leave the <see cref="int"/> range.
    /// </summary>
    public bool TryMove(int offsetX, int offsetY, out GameArea moved)
    {
        var x = (long)X + offsetX;
        var y = (long)Y + offsetY;
        if (x < int.MinValue || y < int.MinValue || x + Width - 1 > int.MaxValue || y + Depth - 1 > int.MaxValue)
        {
            moved = default;
            return false;
        }

        moved = this with { X = (int)x, Y = (int)y };
        return true;
    }
}

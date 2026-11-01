namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// The tiles from <see cref="X"/> to <see cref="Right"/> and from <see cref="Y"/> to <see cref="Top"/>; a tile is 25 cm,
/// X grows to the east and Y to the north.
/// </summary>
public readonly record struct GameArea(int X, int Y, int Width, int Depth)
{
    public long Right => (long)X + Width - 1;

    public long Top => (long)Y + Depth - 1;

    public bool Intersects(GameArea other) =>
        X <= other.Right && other.X <= Right && Y <= other.Top && other.Y <= Top;

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

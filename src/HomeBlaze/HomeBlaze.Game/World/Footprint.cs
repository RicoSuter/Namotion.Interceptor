namespace HomeBlaze.Game.World;

/// <summary>
/// The tiles from <see cref="X"/> to <see cref="Right"/> and from <see cref="Y"/> to <see cref="Top"/>.
/// </summary>
internal readonly record struct Footprint(int X, int Y, int Width, int Depth)
{
    public long Right => (long)X + Width - 1;

    public long Top => (long)Y + Depth - 1;

    public bool Intersects(Footprint other) =>
        X <= other.Right && other.X <= Right && Y <= other.Top && other.Y <= Top;

    public bool Contains(long x, long y) => x >= X && x <= Right && y >= Y && y <= Top;

    /// <summary>
    /// Moves the footprint by the offset; returns false when a tile of the result would leave the <see cref="int"/> range.
    /// </summary>
    public bool TryMove(int offsetX, int offsetY, out Footprint moved)
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

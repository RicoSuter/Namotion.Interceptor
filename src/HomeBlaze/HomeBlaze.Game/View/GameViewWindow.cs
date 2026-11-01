using HomeBlaze.Game.World;

namespace HomeBlaze.Game.View;

/// <summary>
/// The chunks from <see cref="Minimum"/> to <see cref="Maximum"/>, both included, that a page sees.
/// </summary>
internal readonly record struct GameViewWindow((long X, long Y) Minimum, (long X, long Y) Maximum)
{
    /// <summary>
    /// Returns the window of the chunks within <see cref="GameViewTracker.ViewRadius"/> tiles of the tile.
    /// </summary>
    public static GameViewWindow Around(int x, int y) => new(
        GameWorldIndex.GetChunk((long)x - GameViewTracker.ViewRadius, (long)y - GameViewTracker.ViewRadius),
        GameWorldIndex.GetChunk((long)x + GameViewTracker.ViewRadius, (long)y + GameViewTracker.ViewRadius));

    public bool Contains((long X, long Y) chunk) =>
        chunk.X >= Minimum.X && chunk.X <= Maximum.X && chunk.Y >= Minimum.Y && chunk.Y <= Maximum.Y;

    /// <summary>
    /// Whether an edge of the other window lies more than one chunk away from the same edge of this one.
    /// </summary>
    public bool IsFarFrom(GameViewWindow other) =>
        Math.Abs(Minimum.X - other.Minimum.X) > 1 || Math.Abs(Minimum.Y - other.Minimum.Y) > 1 ||
        Math.Abs(Maximum.X - other.Maximum.X) > 1 || Math.Abs(Maximum.Y - other.Maximum.Y) > 1;
}

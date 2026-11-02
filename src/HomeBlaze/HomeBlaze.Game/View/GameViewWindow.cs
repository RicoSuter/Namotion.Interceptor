using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.View;

/// <summary>
/// The chunks from <see cref="Minimum"/> to <see cref="Maximum"/>, both included, that a page sees.
/// </summary>
internal readonly record struct GameViewWindow((long X, long Y) Minimum, (long X, long Y) Maximum)
{
    /// <summary>
    /// The window without chunks.
    /// </summary>
    public static readonly GameViewWindow None = new((0, 0), (-1, -1));

    /// <summary>
    /// Returns the window of the chunks within <see cref="GameViewTracker.ViewRadius"/> tiles of the tile.
    /// </summary>
    public static GameViewWindow Around(int x, int y) => new(
        GameWorldIndex.GetChunk((long)x - GameViewTracker.ViewRadius, (long)y - GameViewTracker.ViewRadius),
        GameWorldIndex.GetChunk((long)x + GameViewTracker.ViewRadius, (long)y + GameViewTracker.ViewRadius));

    /// <summary>
    /// Returns the window of the chunks the area covers.
    /// </summary>
    public static GameViewWindow Of(GameArea area) =>
        new(GameWorldIndex.GetChunk(area.X, area.Y), GameWorldIndex.GetChunk(area.Right, area.Top));

    public bool Contains((long X, long Y) chunk) =>
        chunk.X >= Minimum.X && chunk.X <= Maximum.X && chunk.Y >= Minimum.Y && chunk.Y <= Maximum.Y;

    /// <summary>
    /// Whether the window holds a chunk that the area covers.
    /// </summary>
    public bool Intersects(GameArea area) => Intersects(Of(area));

    public bool Intersects(GameViewWindow other) =>
        Minimum.X <= other.Maximum.X && other.Minimum.X <= Maximum.X && Minimum.Y <= other.Maximum.Y && other.Minimum.Y <= Maximum.Y;

    /// <summary>
    /// Whether the entity is indexed, visible and covers a chunk of the window now.
    /// </summary>
    public bool Holds(GameEntity entity, GameWorldIndex index) =>
        entity.IsVisible && Intersects(entity.Area) && index.Contains(entity);

    /// <summary>
    /// Whether the entity of the change was indexed, visible and covered a chunk of the window before the change.
    /// </summary>
    public bool HeldBefore(in GameEntityChange change) => change.WasVisible && Intersects(change.OldArea);

    /// <summary>
    /// Returns the chunks both windows hold; its minimum exceeds its maximum when there are none.
    /// </summary>
    public GameViewWindow Intersect(GameViewWindow other) => new(
        (Math.Max(Minimum.X, other.Minimum.X), Math.Max(Minimum.Y, other.Minimum.Y)),
        (Math.Min(Maximum.X, other.Maximum.X), Math.Min(Maximum.Y, other.Maximum.Y)));

    /// <summary>
    /// Whether an edge of the other window lies more than one chunk away from the same edge of this one.
    /// </summary>
    public bool IsFarFrom(GameViewWindow other) =>
        Math.Abs(Minimum.X - other.Minimum.X) > 1 || Math.Abs(Minimum.Y - other.Minimum.Y) > 1 ||
        Math.Abs(Maximum.X - other.Maximum.X) > 1 || Math.Abs(Maximum.Y - other.Maximum.Y) > 1;
}

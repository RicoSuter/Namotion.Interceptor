using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Visuals;
using HomeBlaze.Game.World;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game;

/// <summary>
/// Something that stands on the tiles of a game. A tile is 25 cm; the object covers the tiles from <see cref="X"/> to
/// X + <see cref="Width"/> - 1 and from <see cref="Y"/> to Y + <see cref="Depth"/> - 1.
/// </summary>
[InterceptorSubject]
public abstract partial class GameObject : IConfigurable
{
    // Created on first use, because most object types override Visual.
    private GameVisualCache<(int Width, int Height, int Depth)>? _defaultVisual;

    /// <summary>
    /// The largest width, depth and height in tiles.
    /// </summary>
    public const int MaximumSize = 1024;

    /// <summary>
    /// The west edge of the footprint; X grows to the east.
    /// </summary>
    [Configuration]
    public partial int X { get; set; }

    /// <summary>
    /// The south edge of the footprint; Y grows to the north.
    /// </summary>
    [Configuration]
    public partial int Y { get; set; }

    /// <summary>
    /// The width in tiles. Walls and doors configure it; a player has a fixed size and ignores a set value.
    /// </summary>
    public virtual int Width { get; set; } = 1;

    /// <summary>
    /// The depth in tiles. Walls and doors configure it; a player has a fixed size and ignores a set value.
    /// </summary>
    public virtual int Depth { get; set; } = 1;

    /// <summary>
    /// The height in tiles, which only the rendering uses. Walls and doors configure it; a player has a fixed size
    /// and ignores a set value.
    /// </summary>
    public virtual int Height { get; set; } = 1;

    /// <summary>
    /// Whether this object blocks movement, which means no other movement blocking object may enter the tiles of its footprint.
    /// </summary>
    public virtual bool IsMovementBlocking => true;

    /// <summary>
    /// Whether the object stops sound; unlike <see cref="IsMovementBlocking"/>, which is about movement.
    /// </summary>
    public virtual bool IsSoundBlocking => false;

    /// <summary>
    /// Whether players see this object.
    /// </summary>
    public virtual bool IsVisible => true;

    /// <summary>
    /// How the object looks; a gray box filling the footprint and <see cref="Height"/> by default. An override computes it
    /// from configuration or state properties only, which the page observes, and caches it per state, for example with
    /// <see cref="GameVisualCache{TKey}"/>. Players are drawn by the page itself and ignore it.
    /// </summary>
    public virtual GameVisual Visual =>
        (_defaultVisual ??= new()).Get((Width, Height, Depth), static size => GameVisual.Box(size.Width, size.Height, size.Depth));

    internal Footprint Footprint => new(X, Y, Width, Depth);

    internal Game? ParentGame => this.TryGetFirstParent<Game>();

    protected GameObject()
    {
        X = 0;
        Y = 0;
    }

    public virtual Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        ParentGame?.RequestIndexRebuild();
        return Task.CompletedTask;
    }
}

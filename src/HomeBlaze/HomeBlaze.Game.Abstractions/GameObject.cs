using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// Something that stands on the tiles of a game. A tile is 25 cm; the object covers the tiles from <see cref="X"/> to
/// X + <see cref="Width"/> - 1 and from <see cref="Y"/> to Y + <see cref="Depth"/> - 1.
/// </summary>
[InterceptorSubject]
public abstract partial class GameObject : IConfigurable, IGameObjectLifecycle
{
    // Created on first use, because most object types override Visual.
    private GameVisualCache<(int Width, int Height, int Depth)>? _defaultVisual;

    /// <summary>
    /// The largest width, depth and height in tiles.
    /// </summary>
    public const int MaximumSize = 1024;

    /// <summary>
    /// The west edge of the area; X grows to the east.
    /// </summary>
    [Configuration]
    public partial int X { get; set; }

    /// <summary>
    /// The south edge of the area; Y grows to the north.
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
    /// Whether this object blocks movement, which means no other movement blocking object may enter the tiles of its area.
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
    /// How the object looks; a gray box filling the area and <see cref="Height"/> by default. An override computes it
    /// from configuration or state properties only, which the page observes, and caches it per state, for example with
    /// <see cref="GameVisualCache{TKey}"/>. Players are drawn by the page itself and ignore it.
    /// </summary>
    public virtual GameVisual Visual => DefaultVisual;

    /// <summary>
    /// The gray box of the area and <see cref="Height"/>, which is also what an invalid visual falls back to.
    /// </summary>
    public GameVisual DefaultVisual =>
        (_defaultVisual ??= new()).Get((Width, Height, Depth), static size => GameVisual.Box(size.Width, size.Height, size.Depth));

    /// <summary>
    /// The tiles the object covers.
    /// </summary>
    public GameArea Area => new(X, Y, Width, Depth);

    /// <summary>
    /// The game this object is part of.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not part of a game.</exception>
    protected IGameWorld World =>
        this.TryGetFirstParent<IGameWorld>() ?? throw new InvalidOperationException("The object is not part of a game.");

    /// <summary>
    /// Whether the object is part of a game; false while it is being loaded or after it was removed.
    /// </summary>
    protected bool TryGetWorld([NotNullWhen(true)] out IGameWorld? world)
    {
        world = this.TryGetFirstParent<IGameWorld>();
        return world is not null;
    }

    protected GameObject()
    {
        X = 0;
        Y = 0;
    }

    public virtual Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    void IGameObjectLifecycle.OnAddedToWorld() => OnAddedToWorld();

    /// <summary>
    /// Called on the game's own thread when the object enters the world: when the game starts, restarts or loads, and
    /// when the object is placed or added by a replacement, right after the game added it to its objects.
    /// <see cref="World"/> resolves here, which makes it the place to set up schedules.
    /// </summary>
    /// <remarks>
    /// It can run again for the same instance, after a restart, an outside replacement of the objects, or when the object
    /// is removed and placed again, and nothing is called on removal. Set up only schedules here, which removal and a
    /// stop cancel, or keep it idempotent.
    /// </remarks>
    protected virtual void OnAddedToWorld()
    {
    }
}

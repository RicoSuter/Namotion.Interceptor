using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Game.Buildings;

/// <summary>
/// A door that fills an opening in a wall, one tile deep in a wall running east to west or one tile wide in one running
/// north to south; it blocks the tiles it stands on while it is closed.
/// </summary>
[Category("Game")]
[Description("A door that fills an opening in a wall and blocks it while it is closed")]
[DisplayName("Door")]
[SubjectIcon("DoorFront")]
[InterceptorSubject]
public partial class Door : GameObject, ITitleProvider, IIconProvider
{
    private const double LeafThickness = 0.3;
    private const string LeafColor = "#8b5a2b";
    private const double OpenRotation = -90;

    // The latest close schedule; replacing or clearing it disposes the previous one, so at most one is live.
    private IDisposable? _pendingClose;

    private readonly GameVisualCache<(int Width, int Depth, int Height, bool IsOpen, WallMaterial Material)> _visual = new();

    /// <summary>
    /// The height of the door leaf in tiles, 2.1 m; a lintel of the wall fills the opening above it.
    /// </summary>
    public const double LeafHeight = 8.4;

    /// <summary>
    /// The width of the opening in tiles, 1 m.
    /// </summary>
    public const int DefaultLength = Wall.DefaultLength;

    /// <summary>
    /// The height in tiles, the height of a wall.
    /// </summary>
    public const int DefaultHeight = Wall.DefaultHeight;

    /// <summary>
    /// The narrowest door a wall takes in tiles, 50 cm.
    /// </summary>
    public const int MinimumWidth = 2;

    /// <summary>
    /// The widest door a wall takes in tiles, 2 m.
    /// </summary>
    public const int MaximumWidth = 8;

    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Width { get; set; }

    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Depth { get; set; }

    [Configuration]
    [Range(1, MaximumSize)]
    public override partial int Height { get; set; }

    [Configuration]
    public partial bool IsOpen { get; set; }

    /// <summary>
    /// The material of the lintel above the leaf, the one of the wall around the door.
    /// </summary>
    [Configuration]
    public partial WallMaterial Material { get; set; }

    /// <summary>
    /// The time after which an opened door closes itself, by <see cref="Open"/> or by writing <see cref="IsOpen"/>; zero
    /// keeps it open. A change applies from the next opening. While something stands in the doorway, the door tries again
    /// after another delay. Negative values become zero.
    /// </summary>
    [Configuration]
    public partial TimeSpan AutoCloseDelay { get; set; }

    public string? Title => "Door";

    public string? IconName => "DoorFront";

    public override bool IsMovementBlocking => !IsOpen;

    public override bool IsSoundBlocking => !IsOpen;

    public override GameVisual Visual => _visual.Get((Width, Depth, Height, IsOpen, Material),
        static state => CreateVisual(state.Width, state.Depth, state.Height, state.IsOpen, state.Material));

    [Derived]
    [PropertyAttribute("Open", KnownAttributes.IsEnabled)]
    public bool Open_IsEnabled => !IsOpen;

    [Derived]
    [PropertyAttribute("Close", KnownAttributes.IsEnabled)]
    public bool Close_IsEnabled => IsOpen;

    public Door()
    {
        Width = DefaultLength;
        Depth = 1;
        Height = DefaultHeight;
        IsOpen = false;
        Material = WallMaterial.Bricks;
        AutoCloseDelay = TimeSpan.Zero;
    }

    partial void OnAutoCloseDelayChanging(ref TimeSpan newValue, ref bool cancel)
    {
        if (newValue < TimeSpan.Zero)
        {
            newValue = TimeSpan.Zero;
        }
    }

    partial void OnIsOpenChanged(bool newValue)
    {
        if (newValue)
        {
            if (TryGetWorld(out var world))
            {
                ScheduleAutoClose(world);
            }

            return;
        }

        // The handle is read before IsOpen: an opening on another thread after this close stores a new handle, and only
        // the handle of this close may be cleared, never the new one.
        var pending = Volatile.Read(ref _pendingClose);
        if (!IsOpen && pending is not null && Interlocked.CompareExchange(ref _pendingClose, null, pending) == pending)
        {
            pending.Dispose();
        }
    }

    protected override void OnAddedToWorld()
    {
        if (IsOpen)
        {
            ScheduleAutoClose(World);
        }
    }

    /// <exception cref="InvalidOperationException">The door is already open.</exception>
    [Operation(Title = "Open", Icon = "MeetingRoom", Position = 1, Description = "Opens this door, so players can walk through it.")]
    public void Open()
    {
        if (IsOpen)
        {
            throw new InvalidOperationException("The door is already open.");
        }

        IsOpen = true;
    }

    /// <exception cref="InvalidOperationException">The door is already closed, something stands in it, or it is not part of a game.</exception>
    [Operation(Title = "Close", Icon = "DoorFront", Position = 2, Description = "Closes this door, unless something stands in it.")]
    public void Close()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("The door is already closed.");
        }

        if (!World.IsFree(Area, this))
        {
            throw new InvalidOperationException(GameMessages.Blocked);
        }

        IsOpen = false;
    }

    private void ScheduleAutoClose(IGameWorld world)
    {
        if (AutoCloseDelay <= TimeSpan.Zero)
        {
            return;
        }

        var schedule = world.Schedule(this, AutoCloseDelay, CloseOrTryAgain);
        Interlocked.Exchange(ref _pendingClose, schedule)?.Dispose();
    }

    // Runs on the game's thread; a doorway that is still occupied is tried again after another delay.
    private void CloseOrTryAgain()
    {
        if (!IsOpen)
        {
            return;
        }

        if (World.IsFree(Area, this))
        {
            IsOpen = false;
        }
        else
        {
            ScheduleAutoClose(World);
        }
    }

    private static GameVisual CreateVisual(int width, int depth, int height, bool isOpen, WallMaterial material)
    {
        var leafHeight = Math.Min(LeafHeight, height);
        var leaf = CreateLeaf(width, depth, leafHeight, isOpen ? OpenRotation : 0);
        if (height - leafHeight < 0.01)
        {
            return new GameVisual([leaf]);
        }

        var lintel = WallTextures.CreateBox(material, new GameVisualVector(0, leafHeight, 0), new GameVisualVector(width, height - leafHeight, depth));
        return new GameVisual([lintel, leaf]);
    }

    // The leaf hangs on a hinge at the west end of an opening running east to west, or at the south end of one running
    // north to south, and opens toward the north or the west.
    private static GameVisualPart CreateLeaf(int width, int depth, double height, double rotation)
    {
        var isAlongX = width >= depth;
        return new GameVisualPart
        {
            Shape = GameVisualShape.Box,
            Offset = isAlongX ? new GameVisualVector(0, 0, (depth - LeafThickness) / 2) : new GameVisualVector((width - LeafThickness) / 2, 0, 0),
            Size = isAlongX ? new GameVisualVector(width, height, LeafThickness) : new GameVisualVector(LeafThickness, height, depth),
            Pivot = isAlongX ? new GameVisualPoint(0, depth / 2.0) : new GameVisualPoint(width / 2.0, 0),
            Rotation = rotation,
            Color = LeafColor
        };
    }
}

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Visuals;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Game;

/// <summary>
/// A door that fills an opening in a wall, 4 by 1 tiles in a wall running east to west or 1 by 4 tiles in one running
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

    private readonly GameVisualCache<(int Width, int Depth, int Height, bool IsOpen)> _visual = new();

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

    public string? Title => "Door";

    public string? IconName => "DoorFront";

    public override bool IsMovementBlocking => !IsOpen;

    public override bool IsSoundBlocking => !IsOpen;

    public override GameVisual Visual => _visual.Get((Width, Depth, Height, IsOpen),
        static state => CreateVisual(state.Width, state.Depth, state.Height, state.IsOpen));

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
    }

    /// <exception cref="InvalidOperationException">The door is already open or not part of a game.</exception>
    [Operation(Title = "Open", Icon = "MeetingRoom", Position = 1, Description = "Opens this door, so players can walk through it.")]
    public Task OpenAsync(CancellationToken cancellationToken) =>
        GetGame().SetDoorOpenAsync(this, isOpen: true, cancellationToken);

    /// <exception cref="InvalidOperationException">The door is already closed, someone stands in it, or it is not part of a game.</exception>
    [Operation(Title = "Close", Icon = "DoorFront", Position = 2, Description = "Closes this door, unless someone stands in it.")]
    public Task CloseAsync(CancellationToken cancellationToken) =>
        GetGame().SetDoorOpenAsync(this, isOpen: false, cancellationToken);

    private Game GetGame() => ParentGame ?? throw new InvalidOperationException("The door is not part of a game.");

    private static GameVisual CreateVisual(int width, int depth, int height, bool isOpen)
    {
        var leafHeight = Math.Min(LeafHeight, height);
        var leaf = CreateLeaf(width, depth, leafHeight, isOpen ? OpenRotation : 0);
        if (height - leafHeight < 0.01)
        {
            return new GameVisual([leaf]);
        }

        var lintel = WallTextures.CreateBricks(new GameVisualVector(0, leafHeight, 0), new GameVisualVector(width, height - leafHeight, depth));
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

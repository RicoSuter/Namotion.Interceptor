using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.Visuals;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Game.Objects.Buildings;

/// <summary>
/// A wall segment that blocks the tiles it stands on.
/// </summary>
[Category("Game")]
[Description("A wall segment that blocks the tiles it stands on")]
[DisplayName("Wall")]
[SubjectIcon("ViewColumn")]
[InterceptorSubject]
public partial class Wall : GameObject, ITitleProvider, IIconProvider
{
    // 8 tiles (2 m) is above head height, so lower walls let sound pass over them.
    private const int SoundBlockingHeight = 8;

    private readonly GameVisualCache<(int Width, int Height, int Depth, WallMaterial Material)> _visual = new();

    /// <summary>
    /// The length of a built wall in tiles, 1 m.
    /// </summary>
    public const int DefaultLength = 4;

    /// <summary>
    /// The height of a built wall in tiles, 2.5 m.
    /// </summary>
    public const int DefaultHeight = 10;

    /// <summary>
    /// The shortest wall that takes a door: one of <see cref="Door.MinimumWidth"/> tiles with a wall tile on each side.
    /// </summary>
    public const int MinimumDoorWallLength = Door.MinimumWidth + 2;

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
    public partial WallMaterial Material { get; set; }

    public string? Title => "Wall";

    public string? IconName => "ViewColumn";

    public override bool IsSoundBlocking => Height >= SoundBlockingHeight;

    public override GameVisual Visual => _visual.Get((Width, Height, Depth, Material),
        static state => new GameVisual([WallTextures.CreateBox(state.Material, default, new GameVisualVector(state.Width, state.Height, state.Depth))]));

    [Derived]
    [PropertyAttribute("AddDoor", KnownAttributes.IsEnabled)]
    public bool AddDoor_IsEnabled => IsLine && Length >= MinimumDoorWallLength;

    // A wall runs along its longer side; one with both sides above 1 tile is no line.
    private bool IsLine => Width == 1 || Depth == 1;

    private int Length => Math.Max(Width, Depth);

    public Wall()
    {
        Width = DefaultLength;
        Depth = 1;
        Height = DefaultHeight;
        Material = WallMaterial.Bricks;
    }

    /// <exception cref="InvalidOperationException">The wall is not part of a game.</exception>
    [Operation(Title = "Demolish", Icon = "Delete", Position = 1, Description = "Removes this wall from the game.")]
    public Task DemolishAsync(CancellationToken cancellationToken) => GetGame().RemoveAsync(this, cancellationToken);

    /// <summary>
    /// Replaces this wall by a wall before the door, the door and a wall after it, keeping material and height. The door
    /// is centered on the caller's player along the wall, or on the wall without one, and moved inward so that at least
    /// one wall tile remains on each side.
    /// </summary>
    /// <param name="width">The door width in tiles, <see cref="Door.MinimumWidth"/> to <see cref="Door.MaximumWidth"/>;
    /// null for <see cref="Door.DefaultLength"/>.</param>
    /// <param name="caller">The acting player, or null.</param>
    /// <param name="cancellationToken">Cancels saving.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width is out of range.</exception>
    /// <exception cref="InvalidOperationException">The wall is thicker than one tile, too short for the door, not part of
    /// a game, or a piece would overlap another movement blocking object.</exception>
    [Operation(Title = "Add door", Icon = "DoorFront", Position = 2,
        Description = "Replaces the part of this wall in front of you by a door, keeping a wall tile on each side. An empty width makes a door of 1 m.")]
    public Task AddDoorAsync([Range(Door.MinimumWidth, Door.MaximumWidth)] int? width, [FromServices] GameCaller? caller, CancellationToken cancellationToken)
    {
        var doorWidth = width ?? Door.DefaultLength;
        if (doorWidth is < Door.MinimumWidth or > Door.MaximumWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(width), doorWidth, $"A door is {Door.MinimumWidth} to {Door.MaximumWidth} tiles wide.");
        }

        if (!IsLine)
        {
            throw new InvalidOperationException("Only a wall one tile thick can take a door.");
        }

        var fit = Length - 2;
        if (doorWidth > fit)
        {
            throw new InvalidOperationException($"This wall is too short for a door of {doorWidth} tiles; it fits up to {fit}.");
        }

        var game = GetGame();
        return game.ReplaceAsync(this, CreateDoorPieces(doorWidth, game.ResolveActor(caller)), cancellationToken);
    }

    // Works in doubled coordinates, where the center of a player, on the line between two tiles, is a whole number.
    private List<(string NamePrefix, GameObject Replacement)> CreateDoorPieces(int doorWidth, GamePlayer? actor)
    {
        var isAlongX = Width >= Depth;
        long start = isAlongX ? X : Y;
        var length = Length;
        var doubledCenter = 2 * start + length;
        if (actor is not null)
        {
            doubledCenter = 2L * (isAlongX ? actor.X : actor.Y) + GamePlayer.Size;
        }

        var doorStart = Math.Clamp((long)Math.Floor((doubledCenter - doorWidth) / 2.0), start + 1, start + length - 1 - doorWidth);
        return
        [
            ("wall", CreatePiece(isAlongX, start, (int)(doorStart - start))),
            ("door", CreateDoor(isAlongX, doorStart, doorWidth)),
            ("wall", CreatePiece(isAlongX, doorStart + doorWidth, (int)(start + length - doorStart - doorWidth)))
        ];
    }

    private Wall CreatePiece(bool isAlongX, long position, int pieceLength) => new()
    {
        X = isAlongX ? (int)position : X,
        Y = isAlongX ? Y : (int)position,
        Width = isAlongX ? pieceLength : Width,
        Depth = isAlongX ? Depth : pieceLength,
        Height = Height,
        Material = Material
    };

    private Door CreateDoor(bool isAlongX, long position, int doorWidth) => new()
    {
        X = isAlongX ? (int)position : X,
        Y = isAlongX ? Y : (int)position,
        Width = isAlongX ? doorWidth : Width,
        Depth = isAlongX ? Depth : doorWidth,
        Height = Height,
        Material = Material
    };
}

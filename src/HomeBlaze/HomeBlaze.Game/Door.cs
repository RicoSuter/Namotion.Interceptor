using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
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
    /// <summary>
    /// The width of the opening in tiles, 1 m.
    /// </summary>
    public const int DefaultLength = Wall.DefaultLength;

    /// <summary>
    /// The height in tiles, the height of a wall.
    /// </summary>
    public const int DefaultHeight = Wall.DefaultHeight;

    [Configuration]
    public partial bool IsOpen { get; set; }

    public string? Title => "Door";

    public string? IconName => "DoorFront";

    public override bool IsMovementBlocking => !IsOpen;

    public override bool IsSoundBlocking => !IsOpen;

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
}

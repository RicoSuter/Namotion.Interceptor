using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Game.World;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game;

/// <summary>
/// A player of a game, bound to one browser client of the installation. Only that client moves it.
/// </summary>
[Description("A player of a game, bound to one browser client of the installation")]
[DisplayName("Player")]
[SubjectIcon("Person")]
[ExcludeFromCreation]
[InterceptorSubject]
public partial class GamePlayer : GameObject, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The width and depth of the footprint in tiles, 50 cm.
    /// </summary>
    public const int Size = 2;

    /// <summary>
    /// The height in tiles, 175 cm.
    /// </summary>
    public const int BodyHeight = 7;

    [Configuration]
    public partial GameDirection Facing { get; set; }

    /// <summary>
    /// The id of the client this player belongs to, which is its key in <see cref="Game.Players"/>.
    /// </summary>
    [State(Position = 1)]
    public partial string ClientId { get; internal set; }

    /// <summary>
    /// The title of the client, its name or its anonymous title.
    /// </summary>
    [State(Position = 2)]
    public partial string ClientTitle { get; internal set; }

    [State(IsDiscrete = true, Position = 3)]
    public partial bool IsConnected { get; internal set; }

    [Derived]
    public string? Title => string.IsNullOrEmpty(ClientTitle) ? "Player" : ClientTitle;

    public string? IconName => "Person";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

    public override bool IsBlocking => IsConnected;

    public override bool IsVisible => IsConnected;

    // Not intercepted: runtime state of the movement, created by the game under its lock.
    internal StepRateLimiter? RateLimiter { get; set; }

    public GamePlayer()
    {
        Width = Size;
        Depth = Size;
        Height = BodyHeight;
        Facing = GameDirection.North;
        ClientId = string.Empty;
        ClientTitle = string.Empty;
        IsConnected = false;
    }

    [Operation(Title = "Send message", Icon = "Chat", Position = 1, Description = "Shows a message in the browser of this player.")]
    public Task SendMessageAsync(string message, [FromServices] GameCaller? caller, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var game = ParentGame ?? throw new InvalidOperationException("The player is not part of a game.");
        return game.SendMessageAsync(this, game.ResolveActor(caller), message.Trim(), cancellationToken);
    }
}

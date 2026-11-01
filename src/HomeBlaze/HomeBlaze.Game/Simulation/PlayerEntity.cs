using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// A player as the simulation owns it; <see cref="GamePlayer"/> is its facade, which the simulation publishes to.
/// </summary>
internal sealed class PlayerEntity : GameEntity
{
    public PlayerEntity(GamePlayer subject, string clientId)
        : base(subject)
    {
        ClientId = clientId;
        Area = new GameArea(0, 0, GamePlayer.Size, GamePlayer.Size);
        Height = GamePlayer.BodyHeight;
    }

    public new GamePlayer Subject => (GamePlayer)base.Subject;

    public string ClientId { get; }

    public GameDirection Facing { get; set; }

    public bool IsConnected { get; set; }

    public bool IsMicrophoneOn { get; set; }

    public bool IsAudioOn { get; set; }

    public RateLimiter? StepRateLimiter { get; set; }

    public RateLimiter? TurnRateLimiter { get; set; }

    public long? LastMessageTimestamp { get; set; }

    public int? MicrophonePageId { get; set; }

    public HashSet<int>? AudioPageIds { get; set; }

    /// <summary>
    /// Whether a value the facade shows changed since the last publish.
    /// </summary>
    public bool IsPublishPending { get; set; }

    /// <summary>
    /// Sets connected; a connected player blocks movement and is visible.
    /// </summary>
    public void SetConnected(bool isConnected)
    {
        IsConnected = isConnected;
        IsMovementBlocking = isConnected;
        IsVisible = isConnected;
    }
}

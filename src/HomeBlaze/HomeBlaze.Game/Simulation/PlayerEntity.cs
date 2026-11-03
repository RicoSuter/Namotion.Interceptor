using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.People;
using HomeBlaze.Game.View;
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

    /// <summary>
    /// The title of the client, empty when it has none; <see cref="GameEntity.Title"/> falls back to a default then.
    /// </summary>
    public string ClientTitle { get; set; } = string.Empty;

    public GameDirection Facing { get; set; }

    /// <summary>
    /// The resolved person, one of <see cref="GamePeople.Names"/>; empty until <see cref="ReadAppearance"/> ran.
    /// </summary>
    public string Character { get; set; } = string.Empty;

    /// <summary>
    /// The resolved shirt color as <c>#rrggbb</c>; empty until <see cref="ReadAppearance"/> ran.
    /// </summary>
    public string Color { get; set; } = string.Empty;

    public bool IsConnected { get; set; }

    public bool IsMicrophoneOn { get; set; }

    public bool IsAudioOn { get; set; }

    /// <summary>
    /// The animation the player plays, or null.
    /// </summary>
    public string? Animation { get; set; }

    public GameVisualAnimationMode AnimationMode { get; set; }

    /// <summary>
    /// Counts every start, so the same animation twice in a row plays twice.
    /// </summary>
    public int AnimationSequence { get; set; }

    public long AnimationStartedAt { get; set; }

    public RateLimiter? StepRateLimiter { get; set; }

    public RateLimiter? TurnRateLimiter { get; set; }

    public RateLimiter? AnimationRateLimiter { get; set; }

    public long? LastMessageTimestamp { get; set; }

    public int? MicrophonePageId { get; set; }

    public HashSet<int>? AudioPageIds { get; set; }

    /// <summary>
    /// Whether a value the facade shows changed since the last publish.
    /// </summary>
    public bool IsPublishPending { get; set; }

    /// <summary>
    /// Reads the person and color from the subject; returns whether the resolved values changed.
    /// </summary>
    public bool ReadAppearance()
    {
        var character = GamePeople.Resolve(Subject.Character, ClientId);
        var color = GameColors.Resolve(Subject.Color, ClientId);
        if (character == Character && color == Color)
        {
            return false;
        }

        (Character, Color) = (character, color);
        return true;
    }

    /// <summary>
    /// Starts the animation, replacing a running one; a repeated start counts as a new run.
    /// </summary>
    public void StartAnimation(string animation, GameVisualAnimationMode mode, long timestamp)
    {
        Animation = animation;
        AnimationMode = mode;
        AnimationSequence++;
        AnimationStartedAt = timestamp;
    }

    /// <summary>
    /// Ends the animation; returns whether there was one.
    /// </summary>
    public bool ClearAnimation()
    {
        if (Animation is null)
        {
            return false;
        }

        Animation = null;
        return true;
    }

    /// <summary>
    /// Whether a once animation ran for <see cref="GameSimulationSettings.AnimationLimit"/>; a loop never expires.
    /// </summary>
    public bool IsAnimationExpired(TimeProvider timeProvider) =>
        Animation is not null && AnimationMode == GameVisualAnimationMode.Once &&
        timeProvider.GetElapsedTime(AnimationStartedAt) >= GameSimulationSettings.AnimationLimit;

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

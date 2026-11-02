using System.Runtime.CompilerServices;
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.View;

/// <summary>
/// Builds the payload of an entity at most once until the next <see cref="Clear"/>, so the pages of a tick share it. Not
/// thread-safe: only the simulation thread uses it.
/// </summary>
internal sealed class GameViewPayloads
{
    private readonly Dictionary<GameEntity, GameViewPayload> _payloads = new(ReferenceEqualityComparer.Instance);

    // Visuals are immutable and usually cached, so an entity that moves keeps its parts; weak keys drop the parts of a
    // visual nobody holds anymore.
    private readonly ConditionalWeakTable<GameVisual, GameViewPart[]> _parts = new();
    private static readonly ConditionalWeakTable<GameVisual, GameViewPart[]>.CreateValueCallback CreateParts = GameViewPart.FromVisual;
    private readonly ILogger _logger;

    /// <param name="logger">Warns about object types with an invalid visual.</param>
    public GameViewPayloads(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The number of payloads built since the last <see cref="Clear"/>.
    /// </summary>
    public int Count => _payloads.Count;

    /// <summary>
    /// Gets the payload of the entity's current state, built by the first call since the last <see cref="Clear"/>; the
    /// entity must not change before that.
    /// </summary>
    public GameViewPayload Get(GameEntity entity)
    {
        if (!_payloads.TryGetValue(entity, out var payload))
        {
            payload = Create(entity);
            _payloads.Add(entity, payload);
        }

        return payload;
    }

    /// <summary>
    /// Forgets the payloads; call once the entities may change.
    /// </summary>
    public void Clear() => _payloads.Clear();

    /// <summary>
    /// The visual the view compares, null for a player.
    /// </summary>
    public static GameVisual? GetVisual(GameEntity entity) => entity is PlayerEntity ? null : entity.Visual;

    /// <summary>
    /// The facing the view shows, zero for an object.
    /// </summary>
    public static int GetFacing(GameEntity entity) => entity is PlayerEntity player ? (int)player.Facing : 0;

    /// <summary>
    /// The person the view shows, empty for an object.
    /// </summary>
    public static string GetCharacter(GameEntity entity) => entity is PlayerEntity player ? player.Character : string.Empty;

    /// <summary>
    /// The shirt color the view shows, empty for an object.
    /// </summary>
    public static string GetColor(GameEntity entity) => entity is PlayerEntity player ? player.Color : string.Empty;

    public static bool IsMicrophoneOn(GameEntity entity) => entity is PlayerEntity { IsMicrophoneOn: true };

    public static bool IsAudioOn(GameEntity entity) => entity is PlayerEntity { IsAudioOn: true };

    /// <summary>
    /// The animation the view shows, null for an object or a player without one.
    /// </summary>
    public static string? GetAnimation(GameEntity entity) => entity is PlayerEntity player ? player.Animation : null;

    /// <summary>
    /// The mode and start counter of the animation the view shows, zero for an object.
    /// </summary>
    public static (int Mode, int Sequence) GetAnimationRun(GameEntity entity) =>
        entity is PlayerEntity player ? ((int)player.AnimationMode, player.AnimationSequence) : (0, 0);

    private GameViewPayload Create(GameEntity entity)
    {
        var area = entity.Area;
        if (entity is PlayerEntity player)
        {
            return new GameViewPayload(GameViewPayload.PlayerKind, area.X, area.Y, area.Width, area.Depth, entity.Height,
                (int)player.Facing, entity.Title, player.Color, player.Character, entity.IsMovementBlocking, player.IsMicrophoneOn,
                player.IsAudioOn, player.Animation, (int)player.AnimationMode, player.AnimationSequence, []);
        }

        var visual = GameVisualValidator.GetValidVisual(entity.Subject, entity.Visual, _logger);
        return new GameViewPayload(GameViewPayload.ObjectKind, area.X, area.Y, area.Width, area.Depth, entity.Height, 0,
            entity.Title, string.Empty, string.Empty, entity.IsMovementBlocking, false, false, null, 0, 0, _parts.GetValue(visual, CreateParts));
    }
}

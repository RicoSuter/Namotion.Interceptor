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
    private readonly Dictionary<string, string> _playerColors = new();
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

    public static bool IsMicrophoneOn(GameEntity entity) => entity is PlayerEntity { IsMicrophoneOn: true };

    public static bool IsAudioOn(GameEntity entity) => entity is PlayerEntity { IsAudioOn: true };

    private GameViewPayload Create(GameEntity entity)
    {
        var area = entity.Area;
        if (entity is PlayerEntity player)
        {
            return new GameViewPayload(GameViewPayload.PlayerKind, area.X, area.Y, area.Width, area.Depth, entity.Height,
                (int)player.Facing, entity.Title, GetPlayerColor(player.ClientId), entity.IsMovementBlocking, player.IsMicrophoneOn,
                player.IsAudioOn, []);
        }

        var visual = GameVisualValidator.GetValidVisual(entity.Subject, entity.Visual, _logger);
        return new GameViewPayload(GameViewPayload.ObjectKind, area.X, area.Y, area.Width, area.Depth, entity.Height, 0,
            entity.Title, string.Empty, entity.IsMovementBlocking, false, false, GameViewPart.FromVisual(visual));
    }

    // Other objects take their colors from their visual.
    private string GetPlayerColor(string clientId)
    {
        if (!_playerColors.TryGetValue(clientId, out var color))
        {
            color = GameColors.FromId(clientId);
            _playerColors[clientId] = color;
        }

        return color;
    }
}

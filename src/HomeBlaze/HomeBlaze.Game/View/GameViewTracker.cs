using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.View;

/// <summary>
/// Remembers what one page knows and turns the entities in view into the changes it lacks. Not thread-safe: the
/// simulation calls <see cref="Update"/> on its thread and the page reads <see cref="Known"/> and
/// <see cref="GetPlayerColor"/> only after the update's task completed, never while one runs.
/// </summary>
internal sealed class GameViewTracker
{
    /// <summary>
    /// The tiles in each direction a page sees, 50 m.
    /// </summary>
    public const int ViewRadius = 200;

    private static readonly GameViewUpdate Empty = new([], []);

    private readonly Dictionary<GameEntity, GameViewObject> _known = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<GameEntity> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly List<GameEntity> _left = [];
    private readonly Dictionary<string, string> _playerColors = new();
    private int _nextId;

    public IEnumerable<GameViewObject> Known => _known.Values;

    /// <param name="entities">The visible entities in view.</param>
    /// <param name="self">The page's own player, or null.</param>
    /// <param name="logger">Warns about object types with an invalid visual.</param>
    public GameViewUpdate Update(IReadOnlyList<GameEntity> entities, PlayerEntity? self, ILogger logger)
    {
        var upserted = UpsertChanged(entities, self, logger);
        var removed = RemoveUnseen();
        return upserted is null && removed is null ? Empty : new GameViewUpdate(upserted ?? [], removed ?? []);
    }

    // Returns null when nothing changed, so an unchanged view allocates nothing.
    private List<GameViewObject>? UpsertChanged(IReadOnlyList<GameEntity> entities, PlayerEntity? self, ILogger logger)
    {
        List<GameViewObject>? upserted = null;
        _seen.Clear();
        for (var index = 0; index < entities.Count; index++)
        {
            var entity = entities[index];
            _seen.Add(entity);
            var isSelf = ReferenceEquals(entity, self);
            var known = _known.GetValueOrDefault(entity);
            if (known is not null && IsUnchanged(known, entity, isSelf))
            {
                continue;
            }

            var view = CreateView(entity, known?.Id ?? $"o{++_nextId}", isSelf, logger);
            _known[entity] = view;
            (upserted ??= []).Add(view);
        }

        return upserted;
    }

    // Call after UpsertChanged, which collects the seen entities; returns null when every known entity was seen.
    private string[]? RemoveUnseen()
    {
        _left.Clear();
        foreach (var entity in _known.Keys)
        {
            if (!_seen.Contains(entity))
            {
                _left.Add(entity);
            }
        }

        if (_left.Count == 0)
        {
            return null;
        }

        var removed = new string[_left.Count];
        for (var index = 0; index < _left.Count; index++)
        {
            removed[index] = _known[_left[index]].Id;
            _known.Remove(_left[index]);
        }

        _left.Clear();
        return removed;
    }

    private bool IsUnchanged(GameViewObject known, GameEntity entity, bool isSelf) =>
        HasSameArea(known, entity) && HasSameState(known, entity, isSelf) && HasSameLook(known, entity);

    // A cached visual is the same instance, so the reference check spares the value comparison; neither allocates.
    private bool HasSameLook(GameViewObject known, GameEntity entity) =>
        known.IsMovementBlocking == entity.IsMovementBlocking && HasSameLabel(known, entity) && Equals(known.Visual, GetVisual(entity));

    private static bool HasSameArea(GameViewObject known, GameEntity entity) =>
        known.X == entity.Area.X && known.Y == entity.Area.Y && known.Width == entity.Area.Width && known.Depth == entity.Area.Depth;

    private static bool HasSameState(GameViewObject known, GameEntity entity, bool isSelf) =>
        known.Height == entity.Height && known.IsSelf == isSelf && known.Facing == GetFacing(entity) && HasSameVoice(known, entity);

    private static bool HasSameVoice(GameViewObject known, GameEntity entity) =>
        known.IsMicrophoneOn == IsMicrophoneOn(entity) && known.IsAudioOn == IsAudioOn(entity);

    private bool HasSameLabel(GameViewObject known, GameEntity entity) =>
        known.Title == entity.Title && known.Color == GetColor(entity);

    private GameViewObject CreateView(GameEntity entity, string id, bool isSelf, ILogger logger)
    {
        var visual = GetVisual(entity);
        var area = entity.Area;
        return new(id, GetKind(entity), area.X, area.Y, area.Width, area.Depth, entity.Height, GetFacing(entity), entity.Title,
            GetColor(entity), entity.IsMovementBlocking, isSelf, IsMicrophoneOn(entity), IsAudioOn(entity), visual,
            GetParts(entity, visual, logger));
    }

    private static GameVisual? GetVisual(GameEntity entity) => entity is PlayerEntity ? null : entity.Visual;

    private static IReadOnlyList<GameViewPart> GetParts(GameEntity entity, GameVisual? visual, ILogger logger) =>
        entity is PlayerEntity ? [] : GameViewPart.FromVisual(GameVisualValidator.GetValidVisual(entity.Subject, visual, logger));

    private static string GetKind(GameEntity entity) =>
        entity is PlayerEntity ? GameViewObject.PlayerKind : GameViewObject.ObjectKind;

    private static int GetFacing(GameEntity entity) => entity is PlayerEntity player ? (int)player.Facing : 0;

    private static bool IsMicrophoneOn(GameEntity entity) => entity is PlayerEntity { IsMicrophoneOn: true };

    private static bool IsAudioOn(GameEntity entity) => entity is PlayerEntity { IsAudioOn: true };

    // Other objects take their colors from their visual.
    private string GetColor(GameEntity entity) => entity is PlayerEntity player ? GetPlayerColor(player.ClientId) : string.Empty;

    /// <summary>
    /// Gets the color of the player of the client, the one its view object carries.
    /// </summary>
    public string GetPlayerColor(string clientId)
    {
        if (!_playerColors.TryGetValue(clientId, out var color))
        {
            color = GameColors.FromId(clientId);
            _playerColors[clientId] = color;
        }

        return color;
    }
}

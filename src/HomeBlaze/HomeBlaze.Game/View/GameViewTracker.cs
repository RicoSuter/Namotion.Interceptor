using HomeBlaze.Abstractions;
using HomeBlaze.Game.Visuals;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.View;

/// <summary>
/// Remembers what one page knows and turns the objects in view into the changes it lacks. Not thread-safe: the game
/// calls it under its lock, the page reads <see cref="Known"/> and <see cref="GetPlayerColor"/> on its own render
/// thread between updates.
/// </summary>
internal sealed class GameViewTracker
{
    /// <summary>
    /// The tiles in each direction a page sees, 50 m.
    /// </summary>
    public const int ViewRadius = 200;

    private static readonly GameViewUpdate Empty = new([], []);

    private readonly Dictionary<GameObject, GameViewObject> _known = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<GameObject> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly List<GameObject> _left = [];
    private readonly Dictionary<string, string> _playerColors = new();
    private int _nextId;

    public IEnumerable<GameViewObject> Known => _known.Values;

    /// <param name="objects">The visible objects in view.</param>
    /// <param name="self">The page's own player, or null.</param>
    /// <param name="logger">Warns about object types with an invalid visual.</param>
    public GameViewUpdate Update(IReadOnlyList<GameObject> objects, GamePlayer? self, ILogger logger)
    {
        var upserted = UpsertChanged(objects, self, logger);
        var removed = RemoveUnseen();
        return upserted is null && removed is null ? Empty : new GameViewUpdate(upserted ?? [], removed ?? []);
    }

    // Returns null when nothing changed, so an unchanged view allocates nothing.
    private List<GameViewObject>? UpsertChanged(IReadOnlyList<GameObject> objects, GamePlayer? self, ILogger logger)
    {
        List<GameViewObject>? upserted = null;
        _seen.Clear();
        for (var index = 0; index < objects.Count; index++)
        {
            var gameObject = objects[index];
            _seen.Add(gameObject);
            var isSelf = ReferenceEquals(gameObject, self);
            var known = _known.GetValueOrDefault(gameObject);
            if (known is not null && IsUnchanged(known, gameObject, isSelf))
            {
                continue;
            }

            var view = CreateView(gameObject, known?.Id ?? $"o{++_nextId}", isSelf, logger);
            _known[gameObject] = view;
            (upserted ??= []).Add(view);
        }

        return upserted;
    }

    // Call after UpsertChanged, which collects the seen objects; returns null when every known object was seen.
    private string[]? RemoveUnseen()
    {
        _left.Clear();
        foreach (var gameObject in _known.Keys)
        {
            if (!_seen.Contains(gameObject))
            {
                _left.Add(gameObject);
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

        return removed;
    }

    private bool IsUnchanged(GameViewObject known, GameObject gameObject, bool isSelf) =>
        HasSameFootprint(known, gameObject) && HasSameState(known, gameObject, isSelf) && HasSameLook(known, gameObject);

    // A cached visual is the same instance, so the reference check spares the value comparison; neither allocates.
    private bool HasSameLook(GameViewObject known, GameObject gameObject) =>
        HasSamePassage(known, gameObject) && HasSameLabel(known, gameObject) && Equals(known.Visual, GetVisual(gameObject));

    private static bool HasSameFootprint(GameViewObject known, GameObject gameObject) =>
        known.X == gameObject.X && known.Y == gameObject.Y && known.Width == gameObject.Width && known.Depth == gameObject.Depth;

    private static bool HasSameState(GameViewObject known, GameObject gameObject, bool isSelf) =>
        known.Height == gameObject.Height && known.IsSelf == isSelf && known.Facing == GetFacing(gameObject) && HasSameVoice(known, gameObject);

    private static bool HasSameVoice(GameViewObject known, GameObject gameObject) =>
        known.IsMicrophoneOn == IsMicrophoneOn(gameObject) && known.IsAudioOn == IsAudioOn(gameObject);

    private static bool HasSamePassage(GameViewObject known, GameObject gameObject) =>
        known.IsMovementBlocking == gameObject.IsMovementBlocking && known.IsOpen == IsOpen(gameObject);

    private bool HasSameLabel(GameViewObject known, GameObject gameObject) =>
        known.Title == GetTitle(gameObject) && known.Color == GetColor(gameObject);

    private GameViewObject CreateView(GameObject gameObject, string id, bool isSelf, ILogger logger)
    {
        var visual = GetVisual(gameObject);
        return new(id, GetKind(gameObject), gameObject.X, gameObject.Y, gameObject.Width, gameObject.Depth, gameObject.Height,
            GetFacing(gameObject), GetTitle(gameObject), GetColor(gameObject), gameObject.IsMovementBlocking, IsOpen(gameObject), isSelf,
            IsMicrophoneOn(gameObject), IsAudioOn(gameObject), visual, GetParts(gameObject, visual, logger));
    }

    private static GameVisual? GetVisual(GameObject gameObject) => gameObject is GamePlayer ? null : gameObject.Visual;

    private static IReadOnlyList<GameViewPart> GetParts(GameObject gameObject, GameVisual? visual, ILogger logger) =>
        gameObject is GamePlayer ? [] : GameViewPart.FromVisual(GameVisualValidator.GetValidVisual(gameObject, visual, logger));

    private static string GetKind(GameObject gameObject) => gameObject switch
    {
        GamePlayer => GameViewObject.PlayerKind,
        Wall => GameViewObject.WallKind,
        Door => GameViewObject.DoorKind,
        _ => GameViewObject.ObjectKind
    };

    private static int GetFacing(GameObject gameObject) => gameObject is GamePlayer player ? (int)player.Facing : 0;

    private static bool IsOpen(GameObject gameObject) => gameObject is Door { IsOpen: true };

    private static bool IsMicrophoneOn(GameObject gameObject) => gameObject is GamePlayer { IsMicrophoneOn: true };

    private static bool IsAudioOn(GameObject gameObject) => gameObject is GamePlayer { IsAudioOn: true };

    private static string GetTitle(GameObject gameObject) =>
        (gameObject as ITitleProvider)?.Title ?? string.Empty;

    private string GetColor(GameObject gameObject) => gameObject switch
    {
        GamePlayer player => GetPlayerColor(player),
        Door => GameColors.Door,
        _ => GameColors.Wall
    };

    /// <summary>
    /// Gets the color of the player, the one its view object carries.
    /// </summary>
    public string GetPlayerColor(GamePlayer player)
    {
        if (!_playerColors.TryGetValue(player.ClientId, out var color))
        {
            color = GameColors.FromId(player.ClientId);
            _playerColors[player.ClientId] = color;
        }

        return color;
    }
}

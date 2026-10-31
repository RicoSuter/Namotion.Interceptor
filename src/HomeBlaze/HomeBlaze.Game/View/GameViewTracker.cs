namespace HomeBlaze.Game.View;

/// <summary>
/// Remembers what one page knows and turns the objects in view into the changes it lacks. Not thread-safe: the game
/// calls it under its lock, the page reads <see cref="Known"/> on its own render thread between updates.
/// </summary>
internal sealed class GameViewTracker
{
    /// <summary>
    /// The tiles in each direction a page sees, 50 m.
    /// </summary>
    public const int ViewRadius = 200;

    private readonly Dictionary<GameObject, GameViewObject> _known = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<GameObject> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly List<GameObject> _left = [];
    private int _nextId;

    public IEnumerable<GameViewObject> Known => _known.Values;

    public GameViewUpdate Update(IReadOnlyList<GameObject> objects, GamePlayer? self)
    {
        var upserted = new List<GameViewObject>();
        _seen.Clear();
        foreach (var gameObject in objects)
        {
            _seen.Add(gameObject);
            var known = _known.GetValueOrDefault(gameObject);
            var view = CreateView(gameObject, known?.Id ?? $"o{++_nextId}", ReferenceEquals(gameObject, self));
            if (view != known)
            {
                _known[gameObject] = view;
                upserted.Add(view);
            }
        }

        _left.Clear();
        foreach (var gameObject in _known.Keys)
        {
            if (!_seen.Contains(gameObject))
            {
                _left.Add(gameObject);
            }
        }

        var removed = new List<string>(_left.Count);
        foreach (var gameObject in _left)
        {
            removed.Add(_known[gameObject].Id);
            _known.Remove(gameObject);
        }

        return new GameViewUpdate(upserted, removed);
    }

    private static GameViewObject CreateView(GameObject gameObject, string id, bool isSelf) => gameObject switch
    {
        GamePlayer player => new GameViewObject(id, GameViewObject.PlayerKind, player.X, player.Y, player.Width, player.Depth,
            player.Height, (int)player.Facing, player.Title ?? string.Empty, GameColors.FromId(player.ClientId), player.IsBlocking, isSelf),
        Wall wall => new GameViewObject(id, GameViewObject.WallKind, wall.X, wall.Y, wall.Width, wall.Depth,
            wall.Height, 0, wall.Title ?? string.Empty, GameColors.Wall, wall.IsBlocking, false),
        _ => new GameViewObject(id, GameViewObject.ObjectKind, gameObject.X, gameObject.Y, gameObject.Width, gameObject.Depth,
            gameObject.Height, 0, string.Empty, GameColors.Wall, gameObject.IsBlocking, false)
    };
}

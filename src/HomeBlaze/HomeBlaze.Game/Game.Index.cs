using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    private readonly GameWorldIndex _index = new();
    private bool _isIndexStale = true;

    /// <summary>
    /// Makes the next use of the index rebuild it from <see cref="Players"/> and <see cref="Objects"/>.
    /// </summary>
    internal void RequestIndexRebuild()
    {
        lock (_lock)
        {
            _isIndexStale = true;
        }
    }

    // Callers hold _lock.
    private GameWorldIndex GetIndex()
    {
        if (_isIndexStale)
        {
            _index.Clear();
            foreach (var player in Players.Values)
            {
                _index.AddOrUpdate(player);
            }

            foreach (var gameObject in Objects.Values)
            {
                _index.AddOrUpdate(gameObject);
            }

            _isIndexStale = false;
        }

        return _index;
    }
}

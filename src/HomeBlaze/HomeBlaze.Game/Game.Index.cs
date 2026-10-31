using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    private readonly GameWorldIndex _index = new();

    // 1 while the index is stale; set without the lock, so change observers never wait for the game.
    private int _isIndexStale = 1;

    /// <summary>
    /// Makes the next use of the index rebuild it from <see cref="Players"/> and <see cref="Objects"/>; never blocks.
    /// </summary>
    internal void RequestIndexRebuild() => Volatile.Write(ref _isIndexStale, 1);

    // Callers hold _lock. The flag is cleared before the rebuild, so a request during the rebuild makes the next use rebuild again.
    private GameWorldIndex GetIndex()
    {
        if (Interlocked.Exchange(ref _isIndexStale, 0) == 1)
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
        }

        return _index;
    }
}

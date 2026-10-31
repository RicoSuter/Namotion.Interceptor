namespace HomeBlaze.Game.Visuals;

/// <summary>
/// Keeps the visual of the latest state of an object, so that an unchanged state returns the same instance without
/// allocating. Thread-safe: a reader sees the visual of one state, never a mix.
/// </summary>
/// <typeparam name="TKey">The state the visual follows, typically a value tuple of the properties it reads.</typeparam>
public sealed class GameVisualCache<TKey> where TKey : IEquatable<TKey>
{
    private Entry? _entry;

    /// <summary>
    /// Gets the visual of the key, created by <paramref name="create"/> when the key differs from the last one. Pass a
    /// static lambda, so that the call allocates nothing for an unchanged key.
    /// </summary>
    public GameVisual Get(TKey key, Func<TKey, GameVisual> create)
    {
        var entry = Volatile.Read(ref _entry);
        if (entry is null || !entry.Key.Equals(key))
        {
            entry = new Entry(key, create(key));
            Volatile.Write(ref _entry, entry);
        }

        return entry.Visual;
    }

    private sealed class Entry(TKey key, GameVisual visual)
    {
        public TKey Key { get; } = key;

        public GameVisual Visual { get; } = visual;
    }
}

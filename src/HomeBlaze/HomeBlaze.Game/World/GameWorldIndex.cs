namespace HomeBlaze.Game.World;

/// <summary>
/// Finds the objects of a game by their tiles, in chunks of 16 by 16 tiles. Not thread-safe: the game guards it with its lock.
/// </summary>
internal sealed class GameWorldIndex
{
    private const int ChunkShift = 4;

    private readonly Dictionary<(long X, long Y), List<GameObject>> _chunks = new();
    private readonly Dictionary<GameObject, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<GameObject> _found = new(ReferenceEqualityComparer.Instance);

    public int Count => _entries.Count;

    public void Clear()
    {
        _chunks.Clear();
        _entries.Clear();
    }

    /// <summary>
    /// Adds the object, or moves it to its current footprint and blocking and visibility state; leaves out an object
    /// with a width, depth or height outside 1 to <see cref="GameObject.MaximumSize"/>.
    /// </summary>
    public void AddOrUpdate(GameObject gameObject)
    {
        // A loaded configuration skips the range validation, and a huge footprint would fill the chunks.
        if (!HasValidSize(gameObject))
        {
            Remove(gameObject);
            return;
        }

        var entry = new Entry(gameObject.Footprint, gameObject.IsMovementBlocking, gameObject.IsVisible);
        if (_entries.TryGetValue(gameObject, out var existing))
        {
            if (existing == entry)
            {
                return;
            }

            RemoveFromChunks(gameObject, existing.Footprint);
        }

        _entries[gameObject] = entry;
        AddToChunks(gameObject, entry.Footprint);
    }

    public void Remove(GameObject gameObject)
    {
        if (_entries.Remove(gameObject, out var existing))
        {
            RemoveFromChunks(gameObject, existing.Footprint);
        }
    }

    /// <summary>
    /// Whether a blocking object other than <paramref name="ignored"/> covers a tile of the area.
    /// </summary>
    public bool IsBlocked(Footprint area, GameObject? ignored)
    {
        for (var chunkY = (long)area.Y >> ChunkShift; chunkY <= area.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)area.X >> ChunkShift; chunkX <= area.Right >> ChunkShift; chunkX++)
            {
                if (!_chunks.TryGetValue((chunkX, chunkY), out var objects))
                {
                    continue;
                }

                if (IsBlockedByAny(objects, area, ignored))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Adds each visible object that covers a tile of the area to <paramref name="results"/>, once.
    /// </summary>
    public void Query(Footprint area, List<GameObject> results)
    {
        _found.Clear();
        for (var chunkY = (long)area.Y >> ChunkShift; chunkY <= area.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)area.X >> ChunkShift; chunkX <= area.Right >> ChunkShift; chunkX++)
            {
                if (!_chunks.TryGetValue((chunkX, chunkY), out var objects))
                {
                    continue;
                }

                AddVisibleIntersecting(objects, area, results);
            }
        }
    }

    private static bool HasValidSize(GameObject gameObject) =>
        IsValidSize(gameObject.Width) && IsValidSize(gameObject.Depth) && IsValidSize(gameObject.Height);

    private static bool IsValidSize(int size) => size is >= 1 and <= GameObject.MaximumSize;

    private bool IsBlockedByAny(List<GameObject> objects, Footprint area, GameObject? ignored)
    {
        foreach (var gameObject in objects)
        {
            var entry = _entries[gameObject];
            if (entry.IsMovementBlocking && !ReferenceEquals(gameObject, ignored) && entry.Footprint.Intersects(area))
            {
                return true;
            }
        }

        return false;
    }

    private void AddVisibleIntersecting(List<GameObject> objects, Footprint area, List<GameObject> results)
    {
        foreach (var gameObject in objects)
        {
            var entry = _entries[gameObject];
            if (entry.IsVisible && entry.Footprint.Intersects(area) && _found.Add(gameObject))
            {
                results.Add(gameObject);
            }
        }
    }

    private void AddToChunks(GameObject gameObject, Footprint footprint)
    {
        for (var chunkY = (long)footprint.Y >> ChunkShift; chunkY <= footprint.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)footprint.X >> ChunkShift; chunkX <= footprint.Right >> ChunkShift; chunkX++)
            {
                if (!_chunks.TryGetValue((chunkX, chunkY), out var objects))
                {
                    objects = [];
                    _chunks[(chunkX, chunkY)] = objects;
                }

                objects.Add(gameObject);
            }
        }
    }

    private void RemoveFromChunks(GameObject gameObject, Footprint footprint)
    {
        for (var chunkY = (long)footprint.Y >> ChunkShift; chunkY <= footprint.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)footprint.X >> ChunkShift; chunkX <= footprint.Right >> ChunkShift; chunkX++)
            {
                if (_chunks.TryGetValue((chunkX, chunkY), out var objects) && objects.Remove(gameObject) && objects.Count == 0)
                {
                    _chunks.Remove((chunkX, chunkY));
                }
            }
        }
    }

    private readonly record struct Entry(Footprint Footprint, bool IsMovementBlocking, bool IsVisible);
}

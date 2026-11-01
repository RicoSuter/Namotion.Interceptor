using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;

namespace HomeBlaze.Game.World;

/// <summary>
/// Finds the entities of a game by their tiles, in chunks of 16 by 16 tiles, from the values they had at their last
/// <see cref="AddOrUpdate"/>. Not thread-safe: only the simulation thread uses it.
/// </summary>
internal sealed class GameEntityIndex
{
    private const int ChunkShift = 4;
    private const int MaximumPooledLists = 256;

    private readonly Dictionary<(long X, long Y), List<Slot>> _chunks = new();
    private readonly Dictionary<GameEntity, Slot> _slots = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<List<Slot>> _pooledLists = new();

    // Marks the slots a query already returned, so an entity spanning several chunks is returned once without a set.
    private long _queryStamp;

    public int Count => _slots.Count;

    /// <summary>
    /// Increases with every change of the indexed entities' areas, movement blocking, sound blocking or visibility, and
    /// when one is added or removed, so a reader can tell whether anything relevant to movement or sound changed since
    /// it last looked. A change of only the visual or title leaves it unchanged.
    /// </summary>
    public long Version { get; private set; }

    /// <summary>
    /// The chunks whose entities were added, moved, removed or changed, including a change of only the visual or title.
    /// The index only adds to it; its reader clears it.
    /// </summary>
    public ChangedChunks Changes { get; } = new();

    /// <summary>
    /// Returns the chunk that contains the tile.
    /// </summary>
    public static (long X, long Y) GetChunk(long x, long y) => (x >> ChunkShift, y >> ChunkShift);

    /// <summary>
    /// Whether the width and depth of the area and the height are from 1 to <see cref="GameObject.MaximumSize"/>, which
    /// the index requires.
    /// </summary>
    public static bool HasValidSize(GameArea area, int height) =>
        IsValidSize(area.Width) && IsValidSize(area.Depth) && IsValidSize(height);

    /// <summary>
    /// Whether the size is from 1 to <see cref="GameObject.MaximumSize"/>.
    /// </summary>
    public static bool IsValidSize(int size) => size is >= 1 and <= GameObject.MaximumSize;

    public void Clear()
    {
        foreach (var chunk in _chunks.Keys)
        {
            Changes.Add(chunk);
        }

        _chunks.Clear();
        _slots.Clear();
        Version++;
    }

    /// <summary>
    /// Adds the entity, or takes over its current area, movement blocking, sound blocking, visibility, visual and title;
    /// removes an entity whose size is not valid by <see cref="HasValidSize"/>.
    /// </summary>
    public void AddOrUpdate(GameEntity entity)
    {
        // A loaded configuration skips the range validation, and a huge area would fill the chunks.
        if (!HasValidSize(entity.Area, entity.Height))
        {
            Remove(entity);
            return;
        }

        var entry = new Entry(entity.Area, entity.IsMovementBlocking, entity.IsSoundBlocking, entity.IsVisible);
        if (!_slots.TryGetValue(entity, out var slot))
        {
            slot = new Slot(entity) { Entry = entry, Visual = entity.Visual, Title = entity.Title };
            _slots.Add(entity, slot);
            AddToChunks(slot, entry.Area);
            AddChangedChunks(entry.Area);
            Version++;
            return;
        }

        var existing = slot.Entry;
        var isViewChanged = slot.Visual != entity.Visual || slot.Title != entity.Title;
        if (existing == entry && !isViewChanged)
        {
            return;
        }

        slot.Visual = entity.Visual;
        slot.Title = entity.Title;
        if (existing.Area != entry.Area)
        {
            RemoveFromChunks(slot, existing.Area);
            AddChangedChunks(existing.Area);
            AddToChunks(slot, entry.Area);
        }

        AddChangedChunks(entry.Area);
        if (existing != entry)
        {
            slot.Entry = entry;
            Version++;
        }
    }

    public void Remove(GameEntity entity)
    {
        if (_slots.Remove(entity, out var slot))
        {
            RemoveFromChunks(slot, slot.Entry.Area);
            AddChangedChunks(slot.Entry.Area);
            Version++;
        }
    }

    /// <summary>
    /// Whether a movement blocking entity other than <paramref name="ignored"/> covers a tile of the area.
    /// </summary>
    public bool IsBlocked(GameArea area, GameEntity? ignored)
    {
        for (var chunkY = (long)area.Y >> ChunkShift; chunkY <= area.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)area.X >> ChunkShift; chunkX <= area.Right >> ChunkShift; chunkX++)
            {
                if (_chunks.TryGetValue((chunkX, chunkY), out var slots) && IsBlockedByAny(slots, area, ignored))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a movement blocking entity other than <paramref name="self"/> covers a tile of the area without already
    /// overlapping the current <see cref="GameEntity.Area"/> of <paramref name="self"/>, so an entity can always step
    /// out of what it stands in.
    /// </summary>
    public bool IsBlockedExceptOverlapping(GameArea area, GameEntity self)
    {
        var selfArea = self.Area;
        for (var chunkY = (long)area.Y >> ChunkShift; chunkY <= area.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)area.X >> ChunkShift; chunkX <= area.Right >> ChunkShift; chunkX++)
            {
                if (_chunks.TryGetValue((chunkX, chunkY), out var slots) && IsBlockedByAnyExceptOverlapping(slots, area, self, selfArea))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Adds each visible entity that covers a tile of the area to <paramref name="results"/>, once.
    /// </summary>
    public void Query(GameArea area, List<GameEntity> results)
    {
        var stamp = ++_queryStamp;
        for (var chunkY = (long)area.Y >> ChunkShift; chunkY <= area.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)area.X >> ChunkShift; chunkX <= area.Right >> ChunkShift; chunkX++)
            {
                if (_chunks.TryGetValue((chunkX, chunkY), out var slots))
                {
                    AddVisibleIntersecting(slots, area, results, stamp);
                }
            }
        }
    }

    /// <summary>
    /// Adds each entity that covers a tile of the chunk to <paramref name="results"/>, whether blocking or visible or not.
    /// </summary>
    public void QueryChunk((long X, long Y) chunk, List<GameEntity> results)
    {
        if (!_chunks.TryGetValue(chunk, out var slots))
        {
            return;
        }

        foreach (var slot in slots)
        {
            results.Add(slot.Entity);
        }
    }

    /// <summary>
    /// Whether a sound blocking entity covers the tile, whether visible or not.
    /// </summary>
    public bool IsSoundBlocked(long x, long y)
    {
        if (!_chunks.TryGetValue(GetChunk(x, y), out var slots))
        {
            return false;
        }

        foreach (var slot in slots)
        {
            if (slot.Entry.IsSoundBlocking && slot.Entry.Area.Contains(x, y))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds the area of each entity that blocks sound by <see cref="IsSoundBlocked"/> and covers a tile of the area to
    /// <paramref name="results"/>, once.
    /// </summary>
    public void QuerySoundBlocking(GameArea area, List<GameArea> results)
    {
        var stamp = ++_queryStamp;
        for (var chunkY = (long)area.Y >> ChunkShift; chunkY <= area.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)area.X >> ChunkShift; chunkX <= area.Right >> ChunkShift; chunkX++)
            {
                if (_chunks.TryGetValue((chunkX, chunkY), out var slots))
                {
                    AddSoundBlockingIntersecting(slots, area, results, stamp);
                }
            }
        }
    }

    private static bool IsBlockedByAny(List<Slot> slots, GameArea area, GameEntity? ignored)
    {
        foreach (var slot in slots)
        {
            if (slot.Entry.IsMovementBlocking && !ReferenceEquals(slot.Entity, ignored) && slot.Entry.Area.Intersects(area))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBlockedByAnyExceptOverlapping(List<Slot> slots, GameArea area, GameEntity self, GameArea selfArea)
    {
        foreach (var slot in slots)
        {
            var entry = slot.Entry;
            if (entry.IsMovementBlocking && !ReferenceEquals(slot.Entity, self) && entry.Area.Intersects(area)
                && !entry.Area.Intersects(selfArea))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddVisibleIntersecting(List<Slot> slots, GameArea area, List<GameEntity> results, long stamp)
    {
        foreach (var slot in slots)
        {
            if (slot.QueryStamp != stamp && slot.Entry.IsVisible && slot.Entry.Area.Intersects(area))
            {
                slot.QueryStamp = stamp;
                results.Add(slot.Entity);
            }
        }
    }

    private static void AddSoundBlockingIntersecting(List<Slot> slots, GameArea area, List<GameArea> results, long stamp)
    {
        foreach (var slot in slots)
        {
            if (slot.QueryStamp != stamp && slot.Entry.IsSoundBlocking && slot.Entry.Area.Intersects(area))
            {
                slot.QueryStamp = stamp;
                results.Add(slot.Entry.Area);
            }
        }
    }

    private void AddChangedChunks(GameArea area)
    {
        for (var chunkY = (long)area.Y >> ChunkShift; chunkY <= area.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)area.X >> ChunkShift; chunkX <= area.Right >> ChunkShift; chunkX++)
            {
                Changes.Add((chunkX, chunkY));
            }
        }
    }

    private void AddToChunks(Slot slot, GameArea area)
    {
        for (var chunkY = (long)area.Y >> ChunkShift; chunkY <= area.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)area.X >> ChunkShift; chunkX <= area.Right >> ChunkShift; chunkX++)
            {
                if (!_chunks.TryGetValue((chunkX, chunkY), out var slots))
                {
                    slots = _pooledLists.Count > 0 ? _pooledLists.Pop() : [];
                    _chunks[(chunkX, chunkY)] = slots;
                }

                slots.Add(slot);
            }
        }
    }

    private void RemoveFromChunks(Slot slot, GameArea area)
    {
        for (var chunkY = (long)area.Y >> ChunkShift; chunkY <= area.Top >> ChunkShift; chunkY++)
        {
            for (var chunkX = (long)area.X >> ChunkShift; chunkX <= area.Right >> ChunkShift; chunkX++)
            {
                RemoveFromChunk((chunkX, chunkY), slot);
            }
        }
    }

    private void RemoveFromChunk((long X, long Y) chunk, Slot slot)
    {
        if (!_chunks.TryGetValue(chunk, out var slots) || !slots.Remove(slot) || slots.Count > 0)
        {
            return;
        }

        _chunks.Remove(chunk);
        if (_pooledLists.Count < MaximumPooledLists)
        {
            _pooledLists.Push(slots);
        }
    }

    private readonly record struct Entry(GameArea Area, bool IsMovementBlocking, bool IsSoundBlocking, bool IsVisible);

    /// <summary>
    /// The indexed state of an entity, shared by the lists of all chunks it covers, so a query reads it without a lookup.
    /// </summary>
    private sealed class Slot(GameEntity entity)
    {
        public GameEntity Entity { get; } = entity;

        public Entry Entry { get; set; }

        public GameVisual? Visual { get; set; }

        public string Title { get; set; } = string.Empty;

        public long QueryStamp { get; set; }
    }
}

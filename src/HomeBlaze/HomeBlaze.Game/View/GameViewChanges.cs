using System.Runtime.InteropServices;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.View;

/// <summary>
/// Finds the entity changes of a tick near a window without scanning all of them: the first call after a
/// <see cref="Clear"/> sorts the changes into buckets of 16 by 16 chunks by the area each entity had before and has now.
/// Not thread-safe: only the simulation thread uses it.
/// </summary>
internal sealed class GameViewChanges
{
    private const int BucketShift = 4;

    // Every this many sorts, the buckets that stayed empty since the last pruning are dropped.
    private const int PruneInterval = 256;

    // Larger lists and stamp arrays are dropped after a burst rather than kept.
    private const int MaximumKeptCapacity = 1024;

    // Kept over the ticks, so the buckets of a busy area allocate nothing once they exist.
    private readonly Dictionary<(long X, long Y), Bucket> _buckets = new();
    private readonly List<Bucket> _filledBuckets = [];
    private readonly List<(long X, long Y)> _prunedKeys = [];
    private readonly List<GameEntityChange> _results = [];

    // Per change the collection that last returned it, so a change in several buckets of a window is returned once.
    private int[] _collectedStamps = [];
    private int _stamp;
    private bool _isSorted;
    private long _sortCount;

    /// <summary>
    /// Gets the changes of <see cref="GameWorldIndex.EntityChanges"/> whose entity covered a chunk of the window before
    /// the change or covers one now, and maybe a few more nearby; valid until the next call or <see cref="Clear"/>. The
    /// changes of one entity come in the order they were recorded, which a reader that keeps the first of an entity
    /// relies on. The index must not change before <see cref="Clear"/>.
    /// </summary>
    public ReadOnlySpan<GameEntityChange> Collect(GameWorldIndex index, GameViewWindow window)
    {
        _results.Clear();
        var changes = index.EntityChanges.AsSpan();
        if (changes.Length == 0)
        {
            return [];
        }

        if (!_isSorted)
        {
            Sort(index, changes);
        }

        NextStamp();
        var buckets = GetBuckets(window);
        for (var y = buckets.Minimum.Y; y <= buckets.Maximum.Y; y++)
        {
            for (var x = buckets.Minimum.X; x <= buckets.Maximum.X; x++)
            {
                if (_buckets.TryGetValue((x, y), out var bucket))
                {
                    AddUncollected(bucket.Changes, changes);
                }
            }
        }

        return CollectionsMarshal.AsSpan(_results);
    }

    /// <summary>
    /// Forgets the changes; call before the index changes again.
    /// </summary>
    public void Clear()
    {
        foreach (var bucket in _filledBuckets)
        {
            bucket.Changes.Clear();
            if (bucket.Changes.Capacity > MaximumKeptCapacity)
            {
                bucket.Changes.Capacity = 0;
            }
        }

        _filledBuckets.Clear();
        _results.Clear();
        if (_results.Capacity > MaximumKeptCapacity)
        {
            _results.Capacity = 0;
        }

        if (_isSorted && ++_sortCount % PruneInterval == 0)
        {
            Prune();
        }

        _isSorted = false;
    }

    // Drops the buckets of the areas nobody changed for a while, so the buckets do not pile up as players roam.
    private void Prune()
    {
        foreach (var (key, bucket) in _buckets)
        {
            if (bucket.LastFilledSort <= _sortCount - PruneInterval)
            {
                _prunedKeys.Add(key);
            }
        }

        foreach (var key in _prunedKeys)
        {
            _buckets.Remove(key);
        }

        _prunedKeys.Clear();
        if (_collectedStamps.Length > MaximumKeptCapacity)
        {
            _collectedStamps = [];
        }
    }

    private void Sort(GameWorldIndex index, ReadOnlySpan<GameEntityChange> changes)
    {
        if (_collectedStamps.Length < changes.Length)
        {
            _collectedStamps = new int[Math.Max(changes.Length, _collectedStamps.Length * 2)];
            _stamp = 0;
        }

        for (var i = 0; i < changes.Length; i++)
        {
            ref readonly var change = ref changes[i];
            if (change.WasVisible)
            {
                AddToBuckets(i, GameViewWindow.Of(change.OldArea));
            }

            var entity = change.Entity;
            if (entity.IsVisible && index.Contains(entity))
            {
                AddToBuckets(i, GameViewWindow.Of(entity.Area));
            }
        }

        _isSorted = true;
    }

    private void AddToBuckets(int change, GameViewWindow chunks)
    {
        var buckets = GetBuckets(chunks);
        for (var y = buckets.Minimum.Y; y <= buckets.Maximum.Y; y++)
        {
            for (var x = buckets.Minimum.X; x <= buckets.Maximum.X; x++)
            {
                AddToBucket((x, y), change);
            }
        }
    }

    // A change whose areas before and now share a bucket is added to it once. The changes are added in their order, so
    // the earlier change of an entity precedes a later one in every bucket that holds both, and each bucket that holds
    // the later one also holds the earlier one, as both were added by the entity's current area.
    private void AddToBucket((long X, long Y) key, int change)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket();
            _buckets.Add(key, bucket);
        }

        var changes = bucket.Changes;
        if (changes.Count == 0)
        {
            bucket.LastFilledSort = _sortCount;
            _filledBuckets.Add(bucket);
        }
        else if (changes[^1] == change)
        {
            return;
        }

        changes.Add(change);
    }

    private void AddUncollected(List<int> bucket, ReadOnlySpan<GameEntityChange> changes)
    {
        foreach (var change in bucket)
        {
            if (_collectedStamps[change] != _stamp)
            {
                _collectedStamps[change] = _stamp;
                _results.Add(changes[change]);
            }
        }
    }

    private void NextStamp()
    {
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_collectedStamps);
            _stamp = 1;
        }
    }

    private static GameViewWindow GetBuckets(GameViewWindow chunks) => new(
        (chunks.Minimum.X >> BucketShift, chunks.Minimum.Y >> BucketShift),
        (chunks.Maximum.X >> BucketShift, chunks.Maximum.Y >> BucketShift));

    private sealed class Bucket
    {
        public List<int> Changes { get; } = [];

        // The sort count when the bucket last got a change.
        public long LastFilledSort { get; set; }
    }
}

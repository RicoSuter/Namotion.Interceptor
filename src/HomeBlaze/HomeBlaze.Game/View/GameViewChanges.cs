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

    // Kept over the ticks, so the buckets of a busy area allocate nothing once they exist.
    private readonly Dictionary<(long X, long Y), List<int>> _buckets = new();
    private readonly List<List<int>> _filledBuckets = [];
    private readonly List<GameEntityChange> _results = [];

    // Per change the collection that last returned it, so a change in several buckets of a window is returned once.
    private int[] _collectedStamps = [];
    private int _stamp;
    private bool _isSorted;

    /// <summary>
    /// Gets the changes of <see cref="GameWorldIndex.EntityChanges"/> whose entity covered a chunk of the window before
    /// the change or covers one now, and maybe a few more nearby; valid until the next call or <see cref="Clear"/>. The
    /// index must not change before <see cref="Clear"/>.
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
                    AddUncollected(bucket, changes);
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
            bucket.Clear();
        }

        _filledBuckets.Clear();
        _results.Clear();
        _isSorted = false;
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

    // A change whose areas before and now share a bucket is added to it once.
    private void AddToBucket((long X, long Y) key, int change)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            bucket = [];
            _buckets.Add(key, bucket);
        }

        if (bucket.Count == 0)
        {
            _filledBuckets.Add(bucket);
        }
        else if (bucket[^1] == change)
        {
            return;
        }

        bucket.Add(change);
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
}

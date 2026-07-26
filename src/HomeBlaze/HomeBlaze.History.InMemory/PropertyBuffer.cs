using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.InMemory;

internal sealed class PropertyBuffer
{
    private readonly Lock _lock = new();
    private readonly Sample[] _items;
    private readonly int _capacity;
    private int _start;   // index of the oldest sample
    private int _count;
    private long _evictedCount;

    public PropertyBuffer(int capacity, ValueColumn column, bool isUlong)
    {
        _capacity = Math.Max(1, capacity);
        _items = new Sample[_capacity];
        Column = column;
        IsUlong = isUlong;
    }

    public ValueColumn Column { get; }

    public bool IsUlong { get; }

    public long EvictedCount
    {
        get { lock (_lock) { return _evictedCount; } }
    }

    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    public Sample? Oldest
    {
        get { lock (_lock) { return _count == 0 ? null : _items[_start]; } }
    }

    // Timestamp of the oldest retained sample, or null when empty. Cheaper than reading Oldest when
    // only the timestamp is needed (no Sample copy).
    public DateTimeOffset? OldestTimestamp
    {
        get { lock (_lock) { return _count == 0 ? null : _items[_start].Timestamp; } }
    }

    public Sample? Newest
    {
        get { lock (_lock) { return _count == 0 ? null : _items[Index(_count - 1)]; } }
    }

    // Appends (or replaces at an identical timestamp) and reports whether a sample was evicted to make
    // room. The resulting oldest retained timestamp comes out under the same lock: reading it afterwards
    // let a concurrent age sweep run in between, so the store latched its coverage floor at the sweep's
    // cutoff instead of this eviction's boundary and under-claimed the samples still held.
    public bool Append(Sample sample) => Append(sample, out _);

    /// <inheritdoc cref="Append(Sample)"/>
    public bool Append(Sample sample, out DateTimeOffset? oldestRetained)
    {
        lock (_lock)
        {
            var evicted = AppendCore(sample);
            oldestRetained = _count == 0 ? null : _items[_start].Timestamp;
            return evicted;
        }
    }

    private bool AppendCore(Sample sample)
    {
        if (_count > 0)
        {
            var newestIndex = Index(_count - 1);
            var newestTimestamp = _items[newestIndex].Timestamp;
            if (sample.Timestamp < newestTimestamp)
            {
                return InsertOrdered(sample);
            }

            if (sample.Timestamp == newestTimestamp)
            {
                _items[newestIndex] = sample;
                return false;
            }
        }

        var capacityEvicted = _count == _capacity;
        if (capacityEvicted)
        {
            _start = (_start + 1) % _capacity; // overwrite oldest
            _count--;
            _evictedCount++;
        }

        _items[Index(_count)] = sample;
        _count++;
        return capacityEvicted;
    }

    public int EvictOlderThan(DateTimeOffset cutoff)
    {
        lock (_lock)
        {
            var dropped = 0;
            while (_count > 0 && _items[_start].Timestamp < cutoff)
            {
                _start = (_start + 1) % _capacity;
                _count--;
                dropped++;
            }

            _evictedCount += dropped;
            return dropped;
        }
    }

    public List<Sample> Range(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            var result = new List<Sample>();
            var lower = LowerBound(from);             // first index with Timestamp >= from
            for (var logical = lower; logical < _count; logical++)
            {
                var sample = _items[Index(logical)];
                if (sample.Timestamp >= to)
                {
                    break;
                }

                result.Add(sample);
            }

            return result;
        }
    }

    public Sample? AtOrBefore(DateTimeOffset asOf)
    {
        lock (_lock)
        {
            var upper = UpperBound(asOf);             // count of samples with Timestamp <= asOf
            return upper == 0 ? null : _items[Index(upper - 1)];
        }
    }

    private int Index(int logical) => (_start + logical) % _capacity;

    // first logical index whose Timestamp >= target (binary search over the logical order)
    private int LowerBound(DateTimeOffset target)
    {
        var low = 0;
        var high = _count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_items[Index(mid)].Timestamp < target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    // count of logical samples whose Timestamp <= target
    private int UpperBound(DateTimeOffset target)
    {
        var low = 0;
        var high = _count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_items[Index(mid)].Timestamp <= target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private bool InsertOrdered(Sample sample)
    {
        // Rare late-arrival path. Keep the newest _capacity timestamps, matching the ring's
        // normal in-order behavior. A late sample older than everything retained is discarded
        // rather than evicting a newer retained sample to make room for it.
        var position = LowerBound(sample.Timestamp); // first index with Timestamp >= sample.Timestamp
        if (position < _count && _items[Index(position)].Timestamp == sample.Timestamp)
        {
            _items[Index(position)] = sample; // same (path, timestamp) replaces, matching SQLite
            return false;
        }

        var capacityEvicted = _count == _capacity;
        if (capacityEvicted)
        {
            _evictedCount++;
            if (position == 0)
            {
                return true; // the late sample itself is the one dropped
            }

            _start = (_start + 1) % _capacity;
            _count--;
            position--;
        }

        for (var logical = _count; logical > position; logical--)
        {
            _items[Index(logical)] = _items[Index(logical - 1)];
        }

        _items[Index(position)] = sample;
        _count++;
        return capacityEvicted;
    }
}

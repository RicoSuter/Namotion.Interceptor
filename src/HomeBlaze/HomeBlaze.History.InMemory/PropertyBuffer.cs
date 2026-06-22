using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.InMemory;

internal sealed class PropertyBuffer
{
    private readonly object _lock = new();
    private readonly Sample[] _items;
    private readonly int _capacity;
    private int _start;   // index of the oldest sample
    private int _count;

    public PropertyBuffer(int capacity, ValueColumn column, bool isUlong)
    {
        _capacity = Math.Max(1, capacity);
        _items = new Sample[_capacity];
        Column = column;
        IsUlong = isUlong;
    }

    public ValueColumn Column { get; }

    public bool IsUlong { get; }

    public long EvictedCount { get; private set; }

    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    public Sample? Oldest
    {
        get { lock (_lock) { return _count == 0 ? null : _items[_start]; } }
    }

    public Sample? Newest
    {
        get { lock (_lock) { return _count == 0 ? null : _items[Index(_count - 1)]; } }
    }

    public void Append(Sample sample)
    {
        lock (_lock)
        {
            if (_count > 0 && sample.Timestamp < _items[Index(_count - 1)].Timestamp)
            {
                InsertOrdered(sample);
                return;
            }

            if (_count == _capacity)
            {
                _start = (_start + 1) % _capacity; // overwrite oldest
                _count--;
                EvictedCount++;
            }

            _items[Index(_count)] = sample;
            _count++;
        }
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

            EvictedCount += dropped;
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

    private void InsertOrdered(Sample sample)
    {
        // Rare late-arrival path. Drop the oldest first if full so there is room, then shift
        // the tail right by one from the insertion point. O(n) but only for out-of-order samples.
        if (_count == _capacity)
        {
            _start = (_start + 1) % _capacity;
            _count--;
            EvictedCount++;
        }

        var position = LowerBound(sample.Timestamp); // first index with Timestamp >= sample.Timestamp
        for (var logical = _count; logical > position; logical--)
        {
            _items[Index(logical)] = _items[Index(logical - 1)];
        }

        _items[Index(position)] = sample;
        _count++;
    }
}

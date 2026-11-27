using System.Diagnostics;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The actions waiting for their due time, by owner so that removing an owner cancels them. Not thread-safe except for
/// disposing a returned schedule: only the simulation thread uses everything else.
/// </summary>
internal sealed class GameSchedules
{
    private readonly PriorityQueue<Entry, (long DueTimestamp, long Sequence)> _queue = new();
    private readonly Dictionary<object, List<Entry>> _byOwner = new(ReferenceEqualityComparer.Instance);
    private long _sequence;
    private long _tickLimit;
    private long _tickTimestamp;

    /// <summary>
    /// Queues the action for the owner; disposing the result from any thread keeps it from running and releases the action.
    /// </summary>
    /// <remarks>
    /// The due timestamp is never earlier than the current tick's timestamp, which <see cref="TryTakeDue"/> relies on to
    /// leave entries added during a tick for the next one. A disposed entry stays in the owner's list until it comes due
    /// or the owner is canceled.
    /// </remarks>
    public IDisposable Add(object owner, long dueTimestamp, Action action)
    {
        Debug.Assert(dueTimestamp >= _tickTimestamp, "A schedule must not be due before the current tick.");
        var entry = new Entry(owner, action);
        if (!_byOwner.TryGetValue(owner, out var entries))
        {
            entries = [];
            _byOwner[owner] = entries;
        }

        entries.Add(entry);
        _queue.Enqueue(entry, (dueTimestamp, _sequence++));
        return entry;
    }

    /// <summary>
    /// Marks the start of the tick with the given timestamp: entries added from now on are not taken before the next one.
    /// </summary>
    public void BeginTick(long tickTimestamp)
    {
        _tickTimestamp = tickTimestamp;
        _tickLimit = _sequence;
    }

    /// <summary>
    /// Cancels every pending action of the owner.
    /// </summary>
    public void Cancel(object owner)
    {
        if (_byOwner.Remove(owner, out var entries))
        {
            foreach (var entry in entries)
            {
                entry.Dispose();
            }
        }
    }

    /// <summary>
    /// Cancels every pending action.
    /// </summary>
    public void Clear()
    {
        foreach (var entries in _byOwner.Values)
        {
            foreach (var entry in entries)
            {
                entry.Dispose();
            }
        }

        _byOwner.Clear();
        _queue.Clear();
    }

    /// <summary>
    /// Takes the next action added before the current tick and due at or before the timestamp, oldest due time first and
    /// in the order added for equal ones; canceled entries are skipped.
    /// </summary>
    public bool TryTakeDue(long nowTimestamp, out Action action)
    {
        while (_queue.TryPeek(out var entry, out var priority) && priority.DueTimestamp <= nowTimestamp)
        {
            // An entry added during this tick sorts behind every older entry that is due now, because its due time
            // is not earlier than the tick's timestamp, so stopping here leaves nothing older behind.
            if (priority.Sequence >= _tickLimit)
            {
                break;
            }

            _queue.Dequeue();
            Forget(entry);
            var taken = entry.Take();
            if (taken is null)
            {
                continue;
            }

            action = taken;
            return true;
        }

        action = null!;
        return false;
    }

    private void Forget(Entry entry)
    {
        if (_byOwner.TryGetValue(entry.Owner, out var entries))
        {
            entries.Remove(entry);
            if (entries.Count == 0)
            {
                _byOwner.Remove(entry.Owner);
            }
        }
    }

    // Disposing from any thread only clears the action, which the simulation thread reads when the entry comes due.
    private sealed class Entry(object owner, Action action) : IDisposable
    {
        private Action? _action = action;

        public object Owner { get; } = owner;

        public Action? Take() => Volatile.Read(ref _action);

        public void Dispose() => Volatile.Write(ref _action, null);
    }
}

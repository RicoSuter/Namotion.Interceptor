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

    /// <summary>
    /// Queues the action for the owner; disposing the result from any thread keeps it from running.
    /// </summary>
    /// <remarks>
    /// A disposed entry stays in the owner's list until it comes due or the owner is canceled.
    /// </remarks>
    public IDisposable Add(object owner, long dueTimestamp, Action action)
    {
        var entry = new Entry(owner, action);
        _queue.Enqueue(entry, (dueTimestamp, _sequence++));
        if (!_byOwner.TryGetValue(owner, out var entries))
        {
            entries = [];
            _byOwner[owner] = entries;
        }

        entries.Add(entry);
        return entry;
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
    /// Takes the next action due at or before the timestamp, oldest due time first and in the order added for equal
    /// ones; canceled entries are skipped.
    /// </summary>
    public bool TryTakeDue(long nowTimestamp, out Action action)
    {
        while (_queue.TryPeek(out var entry, out var priority) && priority.DueTimestamp <= nowTimestamp)
        {
            _queue.Dequeue();
            Forget(entry);
            if (entry.IsCanceled)
            {
                continue;
            }

            action = entry.Action;
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

    // Disposing from any thread only sets the flag, which the simulation thread reads when the entry comes due.
    private sealed class Entry(object owner, Action action) : IDisposable
    {
        private volatile bool _isCanceled;

        public object Owner { get; } = owner;

        public Action Action { get; } = action;

        public bool IsCanceled => _isCanceled;

        public void Dispose() => _isCanceled = true;
    }
}

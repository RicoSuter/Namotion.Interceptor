namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Bounded, cross-update buffer for subject property updates that were not resolvable
/// at apply time. Under heavy concurrent structural churn a subject can be momentarily
/// unresolvable when its value update applies; instead of dropping the update (permanent
/// divergence), it is buffered here and applied once the subject becomes resolvable in a
/// later apply.
///
/// Thread-safety: not internally synchronized. The buffer is keyed per root subject and all
/// applies for a given root are serialized by the per-root apply lock
/// (<see cref="SubjectUpdateExtensions.GetApplyLock"/>), so there is no concurrent access.
/// </summary>
internal sealed class PendingApplyBuffer
{
    /// <summary>
    /// Maximum number of distinct pending subjects retained. Beyond this the oldest entries
    /// are evicted (counted as genuine loss) to bound memory under the benign churn where
    /// thousands of subjects per cycle are unresolvable because they were removed.
    /// </summary>
    public const int MaxSubjects = 4096;

    /// <summary>Diagnostic counter: pending subject updates recovered (applied once the subject resolved).</summary>
    internal static long RecoveredSubjectUpdateCount;

    private sealed class Entry
    {
        public required Dictionary<string, SubjectPropertyUpdate> Properties { get; init; }

        // Position in the insertion-order list, for O(1) removal on drain/eviction.
        public required LinkedListNode<string> OrderNode { get; init; }
    }

    // Insertion-ordered map: a Dictionary for O(1) lookup/merge, and a LinkedList<string>
    // for O(1) oldest-first eviction and O(1) removal of arbitrary entries on drain
    // (so the order list never accumulates stale ids).
    private readonly Dictionary<string, Entry> _pending = new();
    private readonly LinkedList<string> _insertionOrder = new();

    public bool IsEmpty => _pending.Count == 0;

    /// <summary>
    /// Buffers the property updates for the given subject. If the subject is already pending,
    /// merges per-property keeping the newer one by <see cref="SubjectPropertyUpdate.Timestamp"/>
    /// (incoming wins on ties). Otherwise stores a defensive copy of the dictionary (the caller's
    /// dictionary may be pooled or reused). Evicts the oldest entries when over <see cref="MaxSubjects"/>.
    /// </summary>
    public void Add(string subjectId, Dictionary<string, SubjectPropertyUpdate> properties)
    {
        if (_pending.TryGetValue(subjectId, out var existing))
        {
            foreach (var (propertyName, incoming) in properties)
            {
                if (!existing.Properties.TryGetValue(propertyName, out var current) ||
                    IsAtLeastAsNew(incoming, current))
                {
                    existing.Properties[propertyName] = incoming;
                }
            }
            // Merging does not refresh insertion order: the entry keeps its original
            // age so a continuously-merged-but-never-resolvable subject can still be evicted.
        }
        else
        {
            var orderNode = _insertionOrder.AddLast(subjectId);
            _pending[subjectId] = new Entry
            {
                // Defensive copy: the caller's dictionary may be pooled/reused.
                Properties = new Dictionary<string, SubjectPropertyUpdate>(properties),
                OrderNode = orderNode
            };
        }

        EvictIfOverCapacity();
    }

    /// <summary>
    /// Applies all currently-resolvable pending subjects, removing them from the buffer.
    /// Unresolvable subjects are left pending for a later drain. Must be called at the start
    /// of an apply (before the current update's values) so that recovered/older values are
    /// applied first and any newer values in the current update win on conflict.
    /// </summary>
    public void DrainResolvable(SubjectUpdateApplyContext context)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        // Snapshot ids: ApplyPropertyUpdates can structurally mutate the graph and we must
        // not mutate _pending while iterating it.
        string[] subjectIds = [.. _pending.Keys];
        foreach (var subjectId in subjectIds)
        {
            if (context.SubjectIdRegistry.TryGetSubjectById(subjectId, out var targetSubject) &&
                _pending.Remove(subjectId, out var entry))
            {
                _insertionOrder.Remove(entry.OrderNode);
                SubjectUpdateApplier.ApplyPropertyUpdates(targetSubject, entry.Properties, context);
                Interlocked.Increment(ref RecoveredSubjectUpdateCount);
            }
        }
    }

    private void EvictIfOverCapacity()
    {
        while (_pending.Count > MaxSubjects)
        {
            var oldest = _insertionOrder.First!.Value;
            _insertionOrder.RemoveFirst();
            _pending.Remove(oldest);

            // Eviction is genuine, unrecoverable loss: count it in the existing
            // "dropped" diagnostic so DiagDroppedSubjectUpdateCount now means "genuinely lost".
            Interlocked.Increment(ref SubjectUpdateApplier.DroppedSubjectUpdateCount);
        }
    }

    /// <summary>Incoming wins when its timestamp is greater than or equal to the existing one.</summary>
    private static bool IsAtLeastAsNew(SubjectPropertyUpdate incoming, SubjectPropertyUpdate existing)
    {
        // A missing timestamp is treated as oldest: incoming-with-timestamp beats existing-without,
        // and incoming-without loses to existing-with. Ties (both missing) let incoming win.
        if (incoming.Timestamp is null)
        {
            return existing.Timestamp is null;
        }

        if (existing.Timestamp is null)
        {
            return true;
        }

        return incoming.Timestamp.Value >= existing.Timestamp.Value;
    }
}

using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Transactions;

/// <summary>
/// Handler that writes transaction changes to their associated external sources.
/// Only writes changes that have an associated source; local (no-source) changes are
/// skipped and left for the transaction to apply.
/// </summary>
internal sealed class SourceTransactionWriter : ITransactionWriter
{
    /// <summary>
    /// One source's changes together with their snapshot indices, kept in lockstep so each accepted change
    /// can be marked at its snapshot slot. Used as the per-source grouping value and the multi-source revert state.
    /// </summary>
    private sealed record SourceGroup(List<SubjectPropertyChange> Changes, List<int> Indices);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<SourceWriteResult> WriteToSourcesAsync(
        Memory<SubjectPropertyChange> changes,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // Single pass that reads each property's source exactly once (so a concurrent SetSource/RemoveSource
        // can't be observed inconsistently) and classifies single- vs multi-source. The buffer (single source)
        // and the per-source dictionary (multi source) are allocated lazily, so the common single-source case
        // never allocates the dictionary. Source-less (local) changes are skipped; the transaction applies them.
        // Each source-bound change carries its snapshot index alongside it so the accepted slots can be marked
        // with the confirming source after the write succeeds.

        ISubjectSource? singleSource = null;
        Dictionary<ISubjectSource, SourceGroup>? changesBySource = null;

        SubjectPropertyChange firstChange = default;   // the first source-bound change, held until we know single vs multi
        var firstIndex = 0;                            // its snapshot index, held alongside firstChange
        SubjectPropertyChange[]? buffer = null;        // allocated only once a 2nd same-source change confirms single-source
        int[]? indices = null;                         // snapshot indices parallel to buffer

        var count = 0;
        var span = changes.Span;
        for (var snapshotIndex = 0; snapshotIndex < span.Length; snapshotIndex++)
        {
            var change = span[snapshotIndex];
            if (!change.Property.TryGetSource(out var source))
            {
                continue;
            }

            if (changesBySource is not null)
            {
                AddToSourceGroup(changesBySource, source, change, snapshotIndex);
            }
            else if (singleSource is null)
            {
                // First source-bound change: remember it. The buffer is deferred until a 2nd same-source change
                // confirms single-source, so a multi-source transaction never allocates a buffer it abandons.
                singleSource = source;
                firstChange = change;
                firstIndex = snapshotIndex;
            }
            else if (ReferenceEquals(source, singleSource))
            {
                if (buffer is null)
                {
                    buffer = new SubjectPropertyChange[changes.Length];
                    indices = new int[changes.Length];
                    buffer[count] = firstChange;
                    indices[count++] = firstIndex;
                }
                buffer[count] = change;
                indices![count++] = snapshotIndex;
            }
            else
            {
                // Second distinct source: switch to multi-source, seeding the dict with the single-source
                // prefix (everything seen so far belonged to singleSource since no other source appeared yet).
                var prefixChanges = new List<SubjectPropertyChange>(buffer is null ? 1 : count);
                var prefixIndices = new List<int>(buffer is null ? 1 : count);
                if (buffer is null)
                {
                    prefixChanges.Add(firstChange);
                    prefixIndices.Add(firstIndex);
                }
                else
                {
                    for (var i = 0; i < count; i++)
                    {
                        prefixChanges.Add(buffer[i]);
                        prefixIndices.Add(indices![i]);
                    }
                }
                changesBySource = new Dictionary<ISubjectSource, SourceGroup>
                {
                    [singleSource] = new SourceGroup(prefixChanges, prefixIndices)
                };
                AddToSourceGroup(changesBySource, source, change, snapshotIndex);
            }
        }

        // No source-bound changes: nothing to write. The transaction applies the local changes.
        if (singleSource is null)
        {
            return new SourceWriteResult([], [], [], RevertState: null);
        }

        if (changesBySource is not null)
        {
            return await WriteToMultipleSourcesAsync(changes, changesBySource, requirement, cancellationToken).ConfigureAwait(false);
        }

        // Single source. If only one source-bound change was seen, the buffer was never allocated.
        if (buffer is null)
        {
            buffer = [firstChange];
            indices = [firstIndex];
            count = 1;
        }

        return await WriteToSingleSourceAsync(changes, singleSource, buffer, indices!, count, requirement, cancellationToken).ConfigureAwait(false);
    }

    private static void AddToSourceGroup(Dictionary<ISubjectSource, SourceGroup> changesBySource, ISubjectSource source, SubjectPropertyChange change, int snapshotIndex)
    {
        if (!changesBySource.TryGetValue(source, out var group))
        {
            group = new SourceGroup([], []);
            changesBySource[source] = group;
        }
        group.Changes.Add(change);
        group.Indices.Add(snapshotIndex);
    }

    /// <summary>
    /// Fast path when every source-bound change targets the same source: one write, no grouping or parallel
    /// dispatch. Applies nothing to the local model. <paramref name="buffer"/> holds the source-bound changes in
    /// its first <paramref name="count"/> slots (privately owned, not the commit snapshot), passed without copying;
    /// <paramref name="indices"/> holds their snapshot indices in the same slots. After a successful write the
    /// accepted snapshot slots are marked with <paramref name="source"/>; the returned written set keeps the
    /// privately owned, unmarked copies.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<SourceWriteResult> WriteToSingleSourceAsync(
        Memory<SubjectPropertyChange> snapshot,
        ISubjectSource source,
        SubjectPropertyChange[] buffer,
        int[] indices,
        int count,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // Expose buffer[0..count] without copying: the whole array when exactly full, else an ArraySegment view.
        IReadOnlyList<SubjectPropertyChange> sourceChanges =
            count == buffer.Length ? buffer : new ArraySegment<SubjectPropertyChange>(buffer, 0, count);

        if (requirement == TransactionRequirement.SingleWrite)
        {
            var batchSize = source.WriteBatchSize;
            if (batchSize > 0 && count > batchSize)
            {
                // Nothing was written (validation failed first), so there is no revert state.
                return new SourceWriteResult(
                    [],
                    sourceChanges,
                    [new InvalidOperationException(
                        $"SingleWrite requirement violated: Transaction contains {count} changes for source '{source.GetType().Name}', but WriteBatchSize is {batchSize}.")],
                    RevertState: null);
            }
        }

        // On a reported error only the non-failed changes reached the source (FailedChanges is complete,
        // see WriteChangesInBatchesAsync); on success the whole set did, reused as the written set without
        // copying. RevertState is the source itself: every written change belongs to it.
        var writeResult = await source.WriteChangesInBatchesAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        if (writeResult.Error is null)
        {
            // All changes reached the source: mark every accepted snapshot slot with the confirming source.
            MarkSnapshotSlots(snapshot.Span, indices, count, source);
            return new SourceWriteResult(sourceChanges, [], [], RevertState: source);
        }

        IReadOnlyList<int> sourceIndices =
            count == indices.Length ? indices : new ArraySegment<int>(indices, 0, count);
        var failedSet = ToPropertySet(writeResult.FailedChanges);
        MarkSnapshotSlotsExcept(snapshot.Span, sourceChanges, sourceIndices, failedSet, source);
        var written = ExcludeFailed(sourceChanges, failedSet);
        return new SourceWriteResult(
            written,
            [.. writeResult.FailedChanges],
            [new SourceTransactionWriteException(source, [.. writeResult.FailedChanges], writeResult.Error)],
            RevertState: source);
    }

    /// <summary>
    /// Writes source-bound changes grouped per source and dispatched in parallel. Applies nothing
    /// to the local model. Receives the per-source grouping already built by the single pass; local (no-source)
    /// changes are neither written nor returned. After each per-source write succeeds, that source's accepted
    /// snapshot slots are marked with it.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<SourceWriteResult> WriteToMultipleSourcesAsync(
        Memory<SubjectPropertyChange> snapshot,
        Dictionary<ISubjectSource, SourceGroup> changesBySource,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        if (requirement == TransactionRequirement.SingleWrite)
        {
            var validationError = ValidateSingleWriteRequirement(changesBySource);
            if (validationError != null)
            {
                // Nothing was written (validation failed before any source write), so there is no revert state.
                var validationFailed = FlattenChanges(changesBySource);
                return new SourceWriteResult([], validationFailed, [validationError], RevertState: null);
            }
        }

        if (changesBySource.Count == 0)
        {
            return new SourceWriteResult([], [], [], RevertState: null);
        }

        // RevertState is the per-source grouping already built above (reused as-is, no extra allocation):
        // revert resolves each written change to its original source from this map instead of re-deriving.
        var (written, failed, errors) = await WriteToExternalSourcesAsync(snapshot, changesBySource, cancellationToken).ConfigureAwait(false);
        return new SourceWriteResult(written, failed, errors, RevertState: changesBySource);
    }

    public async ValueTask<SourceRevertResult> RevertSourceWritesAsync(
        IReadOnlyList<SubjectPropertyChange> written,
        object? revertState,
        CancellationToken cancellationToken)
    {
        if (written.Count == 0 || revertState is null)
        {
            return new SourceRevertResult([], []);
        }

        var failed = new List<SubjectPropertyChange>();
        var errors = new List<Exception>();

        if (revertState is ISubjectSource source)
        {
            // Single-source write path: every written change belongs to this exact source.
            // Revert by writing the inverse values (ToRollbackChange) back, without re-deriving the source.
            var single = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>(1)
            {
                [source] = [.. written]
            };
            await TryRevertSourceWritesAsync(single, failed, errors, cancellationToken).ConfigureAwait(false);
            return new SourceRevertResult(failed, errors);
        }

        if (revertState is Dictionary<ISubjectSource, SourceGroup> groups)
        {
            // Multi-source path: revert only the passed 'written' subset, to the ORIGINAL sources recorded at
            // write time (per group, revert the intersection with 'written' so a partial revert hits the right sources).
            var writtenSet = new HashSet<PropertyReference>(written.Count, PropertyReference.Comparer);
            foreach (var change in written)
            {
                writtenSet.Add(change.Property);
            }

            var toRevert = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>(groups.Count);
            foreach (var (groupSource, group) in groups)
            {
                List<SubjectPropertyChange>? subset = null;
                foreach (var change in group.Changes)
                {
                    if (writtenSet.Contains(change.Property))
                    {
                        (subset ??= []).Add(change);
                    }
                }
                if (subset is not null)
                {
                    toRevert[groupSource] = subset;
                }
            }

            await TryRevertSourceWritesAsync(toRevert, failed, errors, cancellationToken).ConfigureAwait(false);
        }

        return new SourceRevertResult(failed, errors);
    }

    /// <summary>
    /// Builds a property set from the failed changes for fast lookup while marking and excluding.
    /// </summary>
    private static HashSet<PropertyReference> ToPropertySet(IReadOnlyList<SubjectPropertyChange> changes)
    {
        var set = new HashSet<PropertyReference>(changes.Count, PropertyReference.Comparer);
        foreach (var change in changes)
        {
            set.Add(change.Property);
        }
        return set;
    }

    /// <summary>
    /// Returns the subset of <paramref name="changes"/> whose property is not in <paramref name="failed"/>.
    /// </summary>
    private static List<SubjectPropertyChange> ExcludeFailed(
        IReadOnlyList<SubjectPropertyChange> changes, HashSet<PropertyReference> failed)
    {
        // Math.Max: a contract-violating source can report failures for properties it was never given.
        var written = new List<SubjectPropertyChange>(Math.Max(0, changes.Count - failed.Count));
        foreach (var change in changes)
        {
            if (!failed.Contains(change.Property))
            {
                written.Add(change);
            }
        }
        return written;
    }

    /// <summary>
    /// Marks the snapshot slots at the first <paramref name="count"/> entries of <paramref name="indices"/>
    /// with the source that accepted them, so the commit's local apply is recognized as an echo by the
    /// outbound connector queue. Must run only after a successful write. Allocation-free.
    /// </summary>
    private static void MarkSnapshotSlots(Span<SubjectPropertyChange> snapshot, int[] indices, int count, ISubjectSource source)
    {
        for (var i = 0; i < count; i++)
        {
            var slot = indices[i];
            snapshot[slot] = snapshot[slot].WithSource(source);
        }
    }

    /// <summary>
    /// Marks the snapshot slots of the changes that were not in <paramref name="failed"/> with the
    /// accepting source, leaving failed and never-written slots untouched. Allocation-free.
    /// </summary>
    private static void MarkSnapshotSlotsExcept(
        Span<SubjectPropertyChange> snapshot,
        IReadOnlyList<SubjectPropertyChange> changes,
        IReadOnlyList<int> indices,
        HashSet<PropertyReference> failed,
        ISubjectSource source)
    {
        for (var i = 0; i < changes.Count; i++)
        {
            if (!failed.Contains(changes[i].Property))
            {
                var slot = indices[i];
                snapshot[slot] = snapshot[slot].WithSource(source);
            }
        }
    }

    /// <summary>
    /// Writes to all external sources in parallel and marks each source's accepted snapshot slots.
    /// </summary>
    private static async Task<(List<SubjectPropertyChange> Written, List<SubjectPropertyChange> Failed, List<Exception> Errors)>
        WriteToExternalSourcesAsync(
            Memory<SubjectPropertyChange> snapshot,
            Dictionary<ISubjectSource, SourceGroup> changesBySource,
            CancellationToken cancellationToken)
    {
        var written = new List<SubjectPropertyChange>();
        var failed = new List<SubjectPropertyChange>();
        var errors = new List<Exception>();

        var taskIndex = 0;
        var writeTasks = new Task<(List<SubjectPropertyChange> Written, List<SubjectPropertyChange> Failed, Exception? Error)>[changesBySource.Count];
        foreach (var kvp in changesBySource)
        {
            writeTasks[taskIndex++] = TryWriteToSourceAsync(snapshot, kvp.Key, kvp.Value, cancellationToken);
        }

        var results = await Task.WhenAll(writeTasks).ConfigureAwait(false);

        foreach (var (writtenList, failList, error) in results)
        {
            written.AddRange(writtenList);
            failed.AddRange(failList);
            if (error != null)
            {
                errors.Add(error);
            }
        }

        return (written, failed, errors);
    }

    /// <summary>
    /// Writes one source's changes and marks its accepted snapshot slots. Each parallel task touches only its
    /// own group's snapshot slots; the single-owner source rule (one source per property) makes the groups
    /// disjoint, so the concurrent marking writes never overlap. Returns the source's written (unmarked) copies,
    /// failed changes, and any error.
    /// </summary>
    private static async Task<(List<SubjectPropertyChange> Written, List<SubjectPropertyChange> Failed, Exception? Error)>
        TryWriteToSourceAsync(
            Memory<SubjectPropertyChange> snapshot,
            ISubjectSource source,
            SourceGroup group,
            CancellationToken cancellationToken)
    {
        var sourceChanges = group.Changes;
        var memory = new ReadOnlyMemory<SubjectPropertyChange>(sourceChanges.ToArray());
        var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken).ConfigureAwait(false);

        if (result.Error is not null)
        {
            var failedSet = ToPropertySet(result.FailedChanges);
            MarkSnapshotSlotsExcept(snapshot.Span, sourceChanges, group.Indices, failedSet, source);
            var written = ExcludeFailed(sourceChanges, failedSet);
            var error = new SourceTransactionWriteException(source, [.. result.FailedChanges], result.Error);
            return (written, [.. result.FailedChanges], error);
        }

        MarkSnapshotSlots(snapshot.Span, group.Indices, source);
        return (sourceChanges, [], null);
    }

    /// <summary>
    /// Marks every snapshot slot in <paramref name="indices"/> with the accepting source. Allocation-free.
    /// </summary>
    private static void MarkSnapshotSlots(Span<SubjectPropertyChange> snapshot, List<int> indices, ISubjectSource source)
    {
        foreach (var slot in indices)
        {
            snapshot[slot] = snapshot[slot].WithSource(source);
        }
    }

    /// <summary>
    /// Attempts to revert successful external source writes.
    /// </summary>
    private static async Task TryRevertSourceWritesAsync(
        Dictionary<ISubjectSource, List<SubjectPropertyChange>> successfulWrites,
        List<SubjectPropertyChange> failedChanges,
        List<Exception> errors,
        CancellationToken cancellationToken)
    {
        foreach (var (source, originalChanges) in successfulWrites)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rollbackChanges = Enumerable.Reverse(originalChanges)
                .Select(c => c.ToRollbackChange())
                .ToArray();

            var memory = new ReadOnlyMemory<SubjectPropertyChange>(rollbackChanges);
            var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken).ConfigureAwait(false);

            if (result.Error is not null)
            {
                var rollbackException = new SourceTransactionWriteException(
                    source,
                    originalChanges,
                    new InvalidOperationException($"Failed to rollback changes to source {source.GetType().Name}", result.Error));

                failedChanges.AddRange(originalChanges);
                errors.Add(rollbackException);
            }
        }
    }

    /// <summary>
    /// Validates that the SingleWrite requirement is satisfied.
    /// </summary>
    private static Exception? ValidateSingleWriteRequirement(
        Dictionary<ISubjectSource, SourceGroup> changesBySource)
    {
        if (changesBySource.Count == 0)
            return null;

        if (changesBySource.Count > 1)
        {
            var sourceNames = new string[changesBySource.Count];
            var nameIndex = 0;
            foreach (var kvp in changesBySource)
            {
                sourceNames[nameIndex++] = kvp.Key.GetType().Name;
            }
            return new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction spans {changesBySource.Count} sources ({string.Join(", ", sourceNames)}), but only 1 is allowed.");
        }

        ISubjectSource? source = null;
        SourceGroup? group = null;
        foreach (var kvp in changesBySource)
        {
            source = kvp.Key;
            group = kvp.Value;
            break;
        }

        // source is always non-null here since we checked Count == 0 above
        var batchSize = source!.WriteBatchSize;
        if (batchSize > 0 && group!.Changes.Count > batchSize)
        {
            return new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction contains {group.Changes.Count} changes for source '{source.GetType().Name}', " +
                $"but WriteBatchSize is {batchSize}.");
        }

        return null;
    }

    /// <summary>
    /// Flattens the per-source groups into a single changes list, in source-grouped order. Avoids LINQ
    /// SelectMany allocation.
    /// </summary>
    private static List<SubjectPropertyChange> FlattenChanges(
        Dictionary<ISubjectSource, SourceGroup> changesBySource)
    {
        var totalCount = 0;
        foreach (var group in changesBySource.Values)
        {
            totalCount += group.Changes.Count;
        }

        var changes = new List<SubjectPropertyChange>(totalCount);
        foreach (var group in changesBySource.Values)
        {
            changes.AddRange(group.Changes);
        }

        return changes;
    }
}

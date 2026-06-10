using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    /// One source's changes together with their snapshot indices, kept in lockstep so the commit can
    /// locate each written change in its snapshot. Used as the per-source grouping value and the
    /// multi-source revert state.
    /// </summary>
    private sealed record SourceGroup(List<SubjectPropertyChange> Changes, List<int> Indices);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<SourceWriteResult> WriteToSourcesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // Single pass that reads each property's source exactly once (so a concurrent SetSource/RemoveSource
        // can't be observed inconsistently) and classifies single- vs multi-source. The buffer (single source)
        // and the per-source dictionary (multi source) are allocated lazily, so the common single-source case
        // never allocates the dictionary. Source-less (local) changes are skipped; the transaction applies them.
        // Each source-bound change carries its snapshot index alongside it so the commit can substitute the
        // source-marked variants without re-matching by property.

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
            return new SourceWriteResult([], [], [], [], RevertState: null);
        }

        if (changesBySource is not null)
        {
            return await WriteToMultipleSourcesAsync(changesBySource, requirement, cancellationToken).ConfigureAwait(false);
        }

        // Single source. If only one source-bound change was seen, the buffer was never allocated.
        if (buffer is null)
        {
            buffer = [firstChange];
            indices = [firstIndex];
            count = 1;
        }

        return await WriteToSingleSourceAsync(singleSource, buffer, indices!, count, requirement, cancellationToken).ConfigureAwait(false);
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
    /// its first <paramref name="count"/> slots (privately owned, not the pooled snapshot), passed without copying;
    /// <paramref name="indices"/> holds their snapshot indices in the same slots.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<SourceWriteResult> WriteToSingleSourceAsync(
        ISubjectSource source,
        SubjectPropertyChange[] buffer,
        int[] indices,
        int count,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // Expose buffer[0..count] / indices[0..count] without copying: the whole array when exactly full,
        // else an ArraySegment view.
        IReadOnlyList<SubjectPropertyChange> sourceChanges =
            count == buffer.Length ? buffer : new ArraySegment<SubjectPropertyChange>(buffer, 0, count);
        IReadOnlyList<int> sourceIndices =
            count == indices.Length ? indices : new ArraySegment<int>(indices, 0, count);

        if (requirement == TransactionRequirement.SingleWrite)
        {
            var batchSize = source.WriteBatchSize;
            if (batchSize > 0 && count > batchSize)
            {
                // Nothing was written (validation failed first), so there is no revert state.
                return new SourceWriteResult(
                    [],
                    [],
                    sourceChanges,
                    [new InvalidOperationException(
                        $"SingleWrite requirement violated: Transaction contains {count} changes for source '{source.GetType().Name}', but WriteBatchSize is {batchSize}.")],
                    RevertState: null);
            }
        }

        // On a reported error only the non-failed changes reached the source; otherwise the whole set did
        // (reused as the written set, no copy). RevertState is the source itself: every written change belongs to it.
        var writeResult = await source.WriteChangesInBatchesAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        if (writeResult.Error is null)
        {
            // Mark in place: the buffer is privately owned and sourceChanges is a view over it.
            MarkConfirmedBySource(buffer.AsSpan(0, count), source);
            return new SourceWriteResult(sourceChanges, sourceIndices, [], [], RevertState: source);
        }

        var (written, writtenIndices) = ExcludeFailed(sourceChanges, sourceIndices, writeResult.FailedChanges);
        MarkConfirmedBySource(CollectionsMarshal.AsSpan(written), source);
        return new SourceWriteResult(
            written,
            writtenIndices,
            [.. writeResult.FailedChanges],
            [new SourceTransactionWriteException(source, [.. writeResult.FailedChanges], writeResult.Error)],
            RevertState: source);
    }

    /// <summary>
    /// Writes source-bound changes grouped per source and dispatched in parallel. Applies nothing
    /// to the local model. Receives the per-source grouping already built by the single pass; local (no-source)
    /// changes are neither written nor returned.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<SourceWriteResult> WriteToMultipleSourcesAsync(
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
                var (validationFailed, _) = FlattenChanges(changesBySource);
                return new SourceWriteResult([], [], validationFailed, [validationError], RevertState: null);
            }
        }

        if (changesBySource.Count == 0)
        {
            return new SourceWriteResult([], [], [], [], RevertState: null);
        }

        // RevertState is the per-source grouping already built above (reused as-is, no extra allocation):
        // revert resolves each written change to its original source from this map instead of re-deriving.
        var (successfulBySource, failed, errors) = await WriteToExternalSourcesAsync(changesBySource, cancellationToken).ConfigureAwait(false);
        var (written, writtenIndices) = FlattenChanges(successfulBySource);
        return new SourceWriteResult(written, writtenIndices, failed, errors, RevertState: changesBySource);
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
    /// Returns the subset of <paramref name="changes"/> that is not in <paramref name="failed"/>, together
    /// with the parallel snapshot <paramref name="indices"/> of the retained changes, filtered in lockstep.
    /// </summary>
    private static (List<SubjectPropertyChange> Written, List<int> WrittenIndices) ExcludeFailed(
        IReadOnlyList<SubjectPropertyChange> changes, IReadOnlyList<int> indices, IReadOnlyList<SubjectPropertyChange> failed)
    {
        var failedSet = new HashSet<SubjectPropertyChange>(failed);
        var written = new List<SubjectPropertyChange>(changes.Count - failed.Count);
        var writtenIndices = new List<int>(changes.Count - failed.Count);
        for (var i = 0; i < changes.Count; i++)
        {
            if (!failedSet.Contains(changes[i]))
            {
                written.Add(changes[i]);
                writtenIndices.Add(indices[i]);
            }
        }
        return (written, writtenIndices);
    }

    /// <summary>
    /// Marks changes as confirmed by the source that accepted them, so the commit's local apply is
    /// recognized as an echo by outbound connector queues. Must run only after a successful write.
    /// </summary>
    private static void MarkConfirmedBySource(Span<SubjectPropertyChange> changes, ISubjectSource source)
    {
        for (var i = 0; i < changes.Length; i++)
        {
            changes[i] = changes[i].WithSource(source);
        }
    }

    /// <summary>
    /// Writes to all external sources in parallel.
    /// </summary>
    private static async Task<(Dictionary<ISubjectSource, SourceGroup> Successful, List<SubjectPropertyChange> Failed, List<Exception> Errors)>
        WriteToExternalSourcesAsync(
            Dictionary<ISubjectSource, SourceGroup> changesBySource,
            CancellationToken cancellationToken)
    {
        var successful = new Dictionary<ISubjectSource, SourceGroup>();
        var failed = new List<SubjectPropertyChange>();
        var errors = new List<Exception>();

        var taskIndex = 0;
        var writeTasks = new Task<(ISubjectSource Source, (SourceGroup Successful, List<SubjectPropertyChange> Failed, Exception? Error) Result)>[changesBySource.Count];
        foreach (var kvp in changesBySource)
        {
            writeTasks[taskIndex++] = WriteToSourceWithResultAsync(kvp.Key, kvp.Value, cancellationToken);
        }

        var results = await Task.WhenAll(writeTasks).ConfigureAwait(false);

        foreach (var (source, (successGroup, failList, error)) in results)
        {
            if (successGroup.Changes.Count > 0)
            {
                successful[source] = successGroup;
            }
            failed.AddRange(failList);
            if (error != null)
            {
                errors.Add(error);
            }
        }

        return (successful, failed, errors);
    }

    /// <summary>
    /// Attempts to write changes to a single external source. Returns the successfully written changes with
    /// their parallel snapshot indices, the failed changes, and any error.
    /// </summary>
    private static async Task<(SourceGroup Successful, List<SubjectPropertyChange> Failed, Exception? Error)>
        TryWriteToSourceAsync(
            ISubjectSource source,
            SourceGroup group,
            CancellationToken cancellationToken)
    {
        var sourceChanges = group.Changes;
        var memory = new ReadOnlyMemory<SubjectPropertyChange>(sourceChanges.ToArray());
        var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken).ConfigureAwait(false);

        if (result.Error is not null)
        {
            var (written, writtenIndices) = ExcludeFailed(sourceChanges, group.Indices, result.FailedChanges);
            MarkConfirmedBySource(CollectionsMarshal.AsSpan(written), source);
            var error = new SourceTransactionWriteException(source, [.. result.FailedChanges], result.Error);
            return (new SourceGroup(written, writtenIndices), [.. result.FailedChanges], error);
        }

        // Safe to mark the shared group list in place: the source already received the unmarked copy
        // via 'memory', and the revert path copying the source into rollback changes is intended.
        MarkConfirmedBySource(CollectionsMarshal.AsSpan(sourceChanges), source);
        return (group, [], null);
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
    /// Flattens the per-source groups into a single changes list and a parallel snapshot-index list, in the
    /// same order. Avoids LINQ SelectMany allocation.
    /// </summary>
    private static (List<SubjectPropertyChange> Changes, List<int> Indices) FlattenChanges(
        Dictionary<ISubjectSource, SourceGroup> changesBySource)
    {
        var totalCount = 0;
        foreach (var group in changesBySource.Values)
        {
            totalCount += group.Changes.Count;
        }

        var changes = new List<SubjectPropertyChange>(totalCount);
        var indices = new List<int>(totalCount);
        foreach (var group in changesBySource.Values)
        {
            changes.AddRange(group.Changes);
            indices.AddRange(group.Indices);
        }

        return (changes, indices);
    }

    /// <summary>
    /// Wraps TryWriteToSourceAsync to include source in result for parallel execution.
    /// </summary>
    private static async Task<(ISubjectSource Source, (SourceGroup Successful, List<SubjectPropertyChange> Failed, Exception? Error) Result)>
        WriteToSourceWithResultAsync(
            ISubjectSource source,
            SourceGroup group,
            CancellationToken cancellationToken)
    {
        var result = await TryWriteToSourceAsync(source, group, cancellationToken).ConfigureAwait(false);
        return (source, result);
    }
}

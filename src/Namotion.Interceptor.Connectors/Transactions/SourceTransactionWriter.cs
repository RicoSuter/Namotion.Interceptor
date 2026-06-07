using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Transactions;

/// <summary>
/// Handler that writes transaction changes to their associated external sources.
/// Only handles changes that have an associated source; local changes are returned
/// to the caller for handling.
/// </summary>
internal sealed class SourceTransactionWriter : ITransactionWriter
{
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<SourceWriteResult> WriteToSourcesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // Read each property's source exactly once: classify (single / multiple / none) and collect the
        // source-less (local) changes in the same pass, so the single-source path never re-reads the
        // mapping (which a concurrent SetSource/RemoveSource could otherwise change between two reads).
        ISubjectSource? singleSource = null;
        var multipleSources = false;
        List<SubjectPropertyChange>? localChanges = null;
        foreach (var change in changes.Span)
        {
            if (change.Property.TryGetSource(out var source))
            {
                if (singleSource is null)
                {
                    singleSource = source;
                }
                else if (!ReferenceEquals(singleSource, source))
                {
                    multipleSources = true;
                    break;
                }
            }
            else
            {
                (localChanges ??= []).Add(change);
            }
        }

        // No source-bound changes: nothing to write. The transaction applies the local changes.
        if (singleSource is null)
        {
            return new SourceWriteResult([], [], []);
        }

        if (!multipleSources)
        {
            return await WriteToSingleSourceAsync(singleSource, changes, localChanges, requirement, cancellationToken).ConfigureAwait(false);
        }

        return await WriteToMultipleSourcesAsync(changes, requirement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Allocation-light path for the common case where every source-bound change targets the same
    /// source: writes once (no per-source grouping or parallel dispatch) and reports the outcome.
    /// Applies nothing in-process. <paramref name="localChanges"/> are the source-less changes already
    /// separated by the single classification pass; they are neither written nor returned.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<SourceWriteResult> WriteToSingleSourceAsync(
        ISubjectSource source,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange>? localChanges,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        var sourceChanges = SeparateSourceChanges(changes, localChanges);

        if (requirement == TransactionRequirement.SingleWrite)
        {
            var batchSize = source.WriteBatchSize;
            if (batchSize > 0 && sourceChanges.Length > batchSize)
            {
                return new SourceWriteResult(
                    [],
                    sourceChanges,
                    [new InvalidOperationException(
                        $"SingleWrite requirement violated: Transaction contains {sourceChanges.Length} changes for source '{source.GetType().Name}', but WriteBatchSize is {batchSize}.")]);
            }
        }

        // Write to the source. On a reported error only the non-failed changes reached it; otherwise
        // the whole array did, so it is reused directly as the written set (no copy).
        var writeResult = await source.WriteChangesInBatchesAsync(sourceChanges, cancellationToken).ConfigureAwait(false);
        if (writeResult.Error is null)
        {
            return new SourceWriteResult(sourceChanges, [], []);
        }

        IReadOnlyList<SubjectPropertyChange> written = ExcludeFailed(sourceChanges, writeResult.FailedChanges);
        return new SourceWriteResult(
            written,
            [.. writeResult.FailedChanges],
            [new SourceTransactionWriteException(source, [.. writeResult.FailedChanges], writeResult.Error)]);
    }

    /// <summary>
    /// Derives the source-bound changes from <paramref name="changes"/> by excluding the already-collected
    /// <paramref name="localChanges"/>, so the property source is never read a second time. The source gets
    /// a private array copy, never the transaction's pooled buffer, so a source that retains the memory is
    /// unaffected when the buffer returns to the pool.
    /// </summary>
    private static SubjectPropertyChange[] SeparateSourceChanges(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        IReadOnlyList<SubjectPropertyChange>? localChanges)
    {
        var local = localChanges ?? (IReadOnlyList<SubjectPropertyChange>)[];
        if (local.Count == 0)
        {
            return changes.ToArray();
        }

        // local is an in-order subsequence of changes (both walk changes.Span in the same order),
        // so a two-pointer walk separates the source-bound changes without an extra set/lookup,
        // filling a single sized array directly (no intermediate list).
        var result = new SubjectPropertyChange[changes.Length - local.Count];
        var localIndex = 0;
        var outIndex = 0;
        foreach (var change in changes.Span)
        {
            if (localIndex < local.Count && local[localIndex].Equals(change))
            {
                localIndex++;
                continue;
            }
            result[outIndex++] = change;
        }
        return result;
    }

    /// <summary>
    /// Writes source-bound changes grouped per source and dispatched in parallel. Applies nothing
    /// in-process. Local (no-source) changes are neither written nor returned.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<SourceWriteResult> WriteToMultipleSourcesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        var externalChangesBySource = GroupSourceBoundChanges(changes.Span);

        if (requirement == TransactionRequirement.SingleWrite)
        {
            var validationError = ValidateSingleWriteRequirement(externalChangesBySource);
            if (validationError != null)
            {
                return new SourceWriteResult([], FlattenChanges(externalChangesBySource), [validationError]);
            }
        }

        if (externalChangesBySource.Count == 0)
        {
            return new SourceWriteResult([], [], []);
        }

        var (successfulBySource, failed, errors) = await WriteToExternalSourcesAsync(externalChangesBySource, cancellationToken).ConfigureAwait(false);
        return new SourceWriteResult(FlattenChanges(successfulBySource), failed, errors);
    }

    public async ValueTask<SourceRevertResult> RevertAsync(
        IReadOnlyList<SubjectPropertyChange> written,
        CancellationToken cancellationToken)
    {
        if (written.Count == 0)
        {
            return new SourceRevertResult([], []);
        }

        var failed = new List<SubjectPropertyChange>();
        var errors = new List<Exception>();

        // Reuse the existing source-revert helper: group the written changes by source and write the
        // inverse values (ToRollbackChanges) back to each source in batches.
        await TryRevertSourceWritesAsync(GroupChangesBySource(written), failed, errors, cancellationToken).ConfigureAwait(false);

        return new SourceRevertResult(failed, errors);
    }

    private static List<SubjectPropertyChange> ExcludeFailed(
        IReadOnlyList<SubjectPropertyChange> changes, IReadOnlyList<SubjectPropertyChange> failed)
    {
        var failedSet = new HashSet<SubjectPropertyChange>(failed);
        var result = new List<SubjectPropertyChange>(changes.Count - failed.Count);
        foreach (var change in changes)
        {
            if (!failedSet.Contains(change))
            {
                result.Add(change);
            }
        }
        return result;
    }

    /// <summary>
    /// Groups changes by their associated source (ignores changes without sources).
    /// </summary>
    private static Dictionary<ISubjectSource, List<SubjectPropertyChange>> GroupChangesBySource(
        IEnumerable<SubjectPropertyChange> changes)
    {
        var result = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>();
        foreach (var change in changes)
        {
            if (change.Property.TryGetSource(out var source))
            {
                if (!result.TryGetValue(source, out var list))
                {
                    list = [];
                    result[source] = list;
                }
                list.Add(change);
            }
        }
        return result;
    }

    /// <summary>
    /// Groups source-bound changes by their associated source (ignores changes without sources).
    /// The writer never handles local changes, so the source-less bucket is not built.
    /// </summary>
    private static Dictionary<ISubjectSource, List<SubjectPropertyChange>>
        GroupSourceBoundChanges(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        var external = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>();

        foreach (var change in changes)
        {
            if (change.Property.TryGetSource(out var source))
            {
                if (!external.TryGetValue(source, out var list))
                {
                    list = [];
                    external[source] = list;
                }
                list.Add(change);
            }
        }

        return external;
    }

    /// <summary>
    /// Writes to all external sources in parallel.
    /// </summary>
    private static async Task<(Dictionary<ISubjectSource, List<SubjectPropertyChange>> Successful, List<SubjectPropertyChange> Failed, List<Exception> Errors)>
        WriteToExternalSourcesAsync(
            Dictionary<ISubjectSource, List<SubjectPropertyChange>> changesBySource,
            CancellationToken cancellationToken)
    {
        var successful = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>();
        var failed = new List<SubjectPropertyChange>();
        var errors = new List<Exception>();

        var taskIndex = 0;
        var writeTasks = new Task<(ISubjectSource Source, (List<SubjectPropertyChange> Successful, List<SubjectPropertyChange> Failed, Exception? Error) Result)>[changesBySource.Count];
        foreach (var kvp in changesBySource)
        {
            writeTasks[taskIndex++] = WriteToSourceWithResultAsync(kvp.Key, kvp.Value, cancellationToken);
        }

        var results = await Task.WhenAll(writeTasks).ConfigureAwait(false);

        foreach (var (source, (successList, failList, error)) in results)
        {
            if (successList.Count > 0)
            {
                successful[source] = successList;
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
    /// Attempts to write changes to a single external source.
    /// </summary>
    private static async Task<(List<SubjectPropertyChange> Successful, List<SubjectPropertyChange> Failed, Exception? Error)>
        TryWriteToSourceAsync(
            ISubjectSource source,
            List<SubjectPropertyChange> sourceChanges,
            CancellationToken cancellationToken)
    {
        var memory = new ReadOnlyMemory<SubjectPropertyChange>(sourceChanges.ToArray());
        var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken).ConfigureAwait(false);

        if (result.Error is not null)
        {
            var written = ExcludeFailed(sourceChanges, result.FailedChanges);
            var error = new SourceTransactionWriteException(source, [.. result.FailedChanges], result.Error);
            return (written, [.. result.FailedChanges], error);
        }

        return (sourceChanges, [], null);
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

            var rollbackChanges = originalChanges.ToRollbackChanges().ToArray();

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
        Dictionary<ISubjectSource, List<SubjectPropertyChange>> externalChangesBySource)
    {
        if (externalChangesBySource.Count == 0)
            return null;

        if (externalChangesBySource.Count > 1)
        {
            var sourceNames = new string[externalChangesBySource.Count];
            var nameIndex = 0;
            foreach (var kvp in externalChangesBySource)
            {
                sourceNames[nameIndex++] = kvp.Key.GetType().Name;
            }
            return new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction spans {externalChangesBySource.Count} sources ({string.Join(", ", sourceNames)}), but only 1 is allowed.");
        }

        ISubjectSource? source = null;
        List<SubjectPropertyChange>? sourceChanges = null;
        foreach (var kvp in externalChangesBySource)
        {
            source = kvp.Key;
            sourceChanges = kvp.Value;
            break;
        }

        // source is always non-null here since we checked Count == 0 above
        var batchSize = source!.WriteBatchSize;
        if (batchSize > 0 && sourceChanges!.Count > batchSize)
        {
            return new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction contains {sourceChanges.Count} changes for source '{source.GetType().Name}', " +
                $"but WriteBatchSize is {batchSize}.");
        }

        return null;
    }

    /// <summary>
    /// Flattens changes from multiple sources into a single list.
    /// Avoids LINQ SelectMany allocation.
    /// </summary>
    private static List<SubjectPropertyChange> FlattenChanges(
        Dictionary<ISubjectSource, List<SubjectPropertyChange>> changesBySource)
    {
        var totalCount = 0;
        foreach (var list in changesBySource.Values)
        {
            totalCount += list.Count;
        }

        var result = new List<SubjectPropertyChange>(totalCount);
        foreach (var list in changesBySource.Values)
        {
            result.AddRange(list);
        }

        return result;
    }

    /// <summary>
    /// Wraps TryWriteToSourceAsync to include source in result for parallel execution.
    /// </summary>
    private static async Task<(ISubjectSource Source, (List<SubjectPropertyChange> Successful, List<SubjectPropertyChange> Failed, Exception? Error) Result)>
        WriteToSourceWithResultAsync(
            ISubjectSource source,
            List<SubjectPropertyChange> sourceChanges,
            CancellationToken cancellationToken)
    {
        var result = await TryWriteToSourceAsync(source, sourceChanges, cancellationToken).ConfigureAwait(false);
        return (source, result);
    }
}

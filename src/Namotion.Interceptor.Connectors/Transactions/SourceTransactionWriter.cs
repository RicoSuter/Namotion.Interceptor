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
    public async Task<TransactionWriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        TransactionFailureHandling failureHandling,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // Fast path when all source-bound changes target one source (common single-connector case);
        // 0 sources or multiple sources fall back to the grouped path. A property's source is fixed
        // for the transaction's lifetime (set at connector attach, cleared at detach, never mid-commit),
        // so re-reading it in WriteSingleSourceAsync stays consistent with this scan.
        ISubjectSource? singleSource = null;
        var multipleSources = false;
        var hasLocal = false;
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
                hasLocal = true;
            }
        }

        if (singleSource is not null && !multipleSources)
        {
            return await WriteSingleSourceAsync(singleSource, changes, hasLocal, failureHandling, requirement, cancellationToken);
        }

        return await WriteGroupedAsync(changes, failureHandling, requirement, cancellationToken);
    }

    /// <summary>
    /// Allocation-light path for the common case where every source-bound change targets the same
    /// source: writes once (no per-source grouping or parallel dispatch) and applies in-process.
    /// Failure and rollback semantics mirror <see cref="WriteGroupedAsync"/>.
    /// </summary>
    private static async Task<TransactionWriteResult> WriteSingleSourceAsync(
        ISubjectSource source,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        bool hasLocal,
        TransactionFailureHandling failureHandling,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // The source gets a private array copy, never the transaction's pooled buffer, so a source that
        // retains the memory is unaffected when the buffer returns to the pool (matches the grouped path).
        SubjectPropertyChange[] sourceChanges;
        IReadOnlyList<SubjectPropertyChange> localChanges;
        if (!hasLocal)
        {
            sourceChanges = changes.ToArray();
            localChanges = [];
        }
        else
        {
            var sourceList = new List<SubjectPropertyChange>(changes.Length);
            var localList = new List<SubjectPropertyChange>();
            foreach (var change in changes.Span)
            {
                if (change.Property.TryGetSource(out _))
                {
                    sourceList.Add(change);
                }
                else
                {
                    localList.Add(change);
                }
            }
            sourceChanges = sourceList.ToArray();
            localChanges = localList;
        }

        if (requirement == TransactionRequirement.SingleWrite)
        {
            var batchSize = source.WriteBatchSize;
            if (batchSize > 0 && sourceChanges.Length > batchSize)
            {
                return new TransactionWriteResult(
                    [],
                    sourceChanges,
                    [new InvalidOperationException(
                        $"SingleWrite requirement violated: Transaction contains {sourceChanges.Length} changes for source '{source.GetType().Name}', but WriteBatchSize is {batchSize}.")],
                    localChanges);
            }
        }

        var allFailed = new List<SubjectPropertyChange>();
        var allErrors = new List<Exception>();

        // Write to the source. On a reported error only the non-failed changes reached it; otherwise
        // the whole array did, so it is reused directly as the written set (no copy).
        var writeResult = await source.WriteChangesInBatchesAsync(sourceChanges, cancellationToken);
        IReadOnlyList<SubjectPropertyChange> written = sourceChanges;
        if (writeResult.Error is not null)
        {
            written = ExcludeFailed(sourceChanges, writeResult.FailedChanges);
            allFailed.AddRange(writeResult.FailedChanges);
            allErrors.Add(new SourceTransactionWriteException(source, [.. writeResult.FailedChanges], writeResult.Error));

            if (failureHandling == TransactionFailureHandling.Rollback)
            {
                await TryRevertSourceWritesAsync(SingleSourceMap(source, written), allFailed, allErrors, cancellationToken);
                return new TransactionWriteResult([], allFailed, allErrors, localChanges);
            }
        }

        // Apply the changes that reached the source to the in-process model.
        var (applied, applyFailed, applyErrors) = written.ApplyAllChanges();
        allFailed.AddRange(applyFailed);
        allErrors.AddRange(applyErrors);

        if (applyFailed.Count > 0)
        {
            if (failureHandling == TransactionFailureHandling.Rollback)
            {
                TryRevertAppliedChanges(applied, allFailed, allErrors);
                await TryRevertSourceWritesAsync(SingleSourceMap(source, written), allFailed, allErrors, cancellationToken);
                return new TransactionWriteResult([], allFailed, allErrors, localChanges);
            }

            // BestEffort: revert only the sources for failed local applies (keep each property in sync).
            await TryRevertSourceWritesAsync(GroupChangesBySource(applyFailed), allFailed, allErrors, cancellationToken);
        }

        return new TransactionWriteResult(applied, allFailed, allErrors, localChanges);
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

    private static Dictionary<ISubjectSource, List<SubjectPropertyChange>> SingleSourceMap(
        ISubjectSource source, IReadOnlyList<SubjectPropertyChange> changes) => new(1) { [source] = [.. changes] };

    private async Task<TransactionWriteResult> WriteGroupedAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        TransactionFailureHandling failureHandling,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // 1. Separate source-bound vs local changes
        var (localChanges, externalChangesBySource) = GroupChangesBySourceType(changes.Span);

        // 2. Validate SingleWrite requirement (only applies to source-bound changes)
        if (requirement == TransactionRequirement.SingleWrite)
        {
            var validationError = ValidateSingleWriteRequirement(externalChangesBySource);
            if (validationError != null)
            {
                var allSourceChanges = FlattenChanges(externalChangesBySource);
                return new TransactionWriteResult([], allSourceChanges, [validationError], localChanges);
            }
        }

        // 3. If no source-bound changes, just return local changes for caller to handle
        if (externalChangesBySource.Count == 0)
        {
            return new TransactionWriteResult([], [], [], localChanges);
        }

        var allSuccessful = new List<SubjectPropertyChange>();
        var allFailed = new List<SubjectPropertyChange>();
        var allErrors = new List<Exception>();

        // 4. Write to external sources in parallel
        var (successfulBySource, failed, errors) = await WriteToExternalSourcesAsync(externalChangesBySource, cancellationToken);
        allFailed.AddRange(failed);
        allErrors.AddRange(errors);

        // If any source failed and rollback mode, revert successful sources
        if (failureHandling == TransactionFailureHandling.Rollback && allFailed.Count > 0)
        {
            await TryRevertSourceWritesAsync(successfulBySource, allFailed, allErrors, cancellationToken);
            return new TransactionWriteResult([], allFailed, allErrors, localChanges);
        }

        // 5. Apply source-bound values to in-process model
        var sourceBoundChanges = FlattenChanges(successfulBySource);
        var (applied, applyFailed, applyErrors) = sourceBoundChanges.ApplyAllChanges();
        allSuccessful.AddRange(applied);
        allFailed.AddRange(applyFailed);
        allErrors.AddRange(applyErrors);

        // If in-process apply failed, rollback sources to maintain consistency
        if (applyFailed.Count > 0)
        {
            if (failureHandling == TransactionFailureHandling.Rollback)
            {
                // Rollback mode: revert everything (all-or-nothing)
                TryRevertAppliedChanges(applied, allFailed, allErrors);
                await TryRevertSourceWritesAsync(successfulBySource, allFailed, allErrors, cancellationToken);
                return new TransactionWriteResult([], allFailed, allErrors, localChanges);
            }

            // BestEffort mode: rollback only sources for failed local applies (keep each property in sync)
            var sourcesToRollback = GroupChangesBySource(applyFailed);
            if (sourcesToRollback.Count > 0)
            {
                await TryRevertSourceWritesAsync(sourcesToRollback, allFailed, allErrors, cancellationToken);
            }
        }

        return new TransactionWriteResult(allSuccessful, allFailed, allErrors, localChanges);
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
    /// Groups changes into local (no source) and external (has source) categories.
    /// </summary>
    private static (List<SubjectPropertyChange> Local, Dictionary<ISubjectSource, List<SubjectPropertyChange>> External)
        GroupChangesBySourceType(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        var local = new List<SubjectPropertyChange>();
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
            else
            {
                local.Add(change);
            }
        }

        return (local, external);
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

        var results = await Task.WhenAll(writeTasks);

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
        var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken);

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
            var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken);

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
    /// Attempts to revert in-process model changes by applying rollback changes.
    /// </summary>
    private static void TryRevertAppliedChanges(
        IReadOnlyList<SubjectPropertyChange> successfulChanges,
        List<SubjectPropertyChange> failedChanges,
        List<Exception> errors)
    {
        var (_, revertFailed, revertErrors) = successfulChanges.ToRollbackChanges().ApplyAllChanges();
        failedChanges.AddRange(revertFailed);
        errors.AddRange(revertErrors);
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
        var result = await TryWriteToSourceAsync(source, sourceChanges, cancellationToken);
        return (source, result);
    }
}

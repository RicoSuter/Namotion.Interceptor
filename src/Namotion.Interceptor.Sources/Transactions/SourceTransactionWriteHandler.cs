using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Transactions;

/// <summary>
/// Handler that writes transaction changes to their associated external sources.
/// Supports different transaction modes including rollback on failure.
/// </summary>
internal sealed class SourceTransactionWriteHandler : ITransactionWriteHandler
{
    public async Task<TransactionWriteResult> WriteChangesAsync(
        IReadOnlyList<SubjectPropertyChange> changes,
        TransactionMode mode,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // 1. Group changes by source
        var changesBySource = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>();
        var changesWithoutSource = new List<SubjectPropertyChange>();

        foreach (var change in changes)
        {
            if (change.Property.TryGetSource(out var source))
            {
                if (!changesBySource.TryGetValue(source, out var list))
                {
                    list = [];
                    changesBySource[source] = list;
                }
                list.Add(change);
            }
            else
            {
                changesWithoutSource.Add(change);
            }
        }

        // 2. Validate SingleWrite requirement
        if (requirement == TransactionRequirement.SingleWrite)
        {
            var validationError = ValidateSingleWriteRequirement(changesBySource, changesWithoutSource);
            if (validationError != null)
            {
                return new TransactionWriteResult(changesWithoutSource, [validationError]);
            }
        }

        // 3. Write to each source, tracking successful writes for potential rollback
        var successfulChanges = new List<SubjectPropertyChange>(changesWithoutSource);
        var successfulSourceWrites = new List<(ISubjectSource Source, List<SubjectPropertyChange> Changes)>();
        var failedChanges = new List<SourceWriteFailure>();

        foreach (var (source, sourceChanges) in changesBySource)
        {
            var memory = new ReadOnlyMemory<SubjectPropertyChange>(sourceChanges.ToArray());
            var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken);

            if (result.Error is not null)
            {
                // Add SourceWriteFailure for each failed change
                // If FailedChanges is empty, all changes in this source failed
                var failedList = result.FailedChanges.Length > 0
                    ? result.FailedChanges.ToArray()
                    : sourceChanges.ToArray();

                foreach (var failedChange in failedList)
                {
                    failedChanges.Add(new SourceWriteFailure(
                        failedChange,
                        source,
                        new SourceWriteException(source, [failedChange], result.Error)));
                }

                // Track successful changes (those not in failed list)
                var failedSet = new HashSet<SubjectPropertyChange>(failedList);
                var writtenList = sourceChanges.Where(c => !failedSet.Contains(c)).ToList();
                if (writtenList.Count > 0)
                {
                    successfulChanges.AddRange(writtenList);
                    successfulSourceWrites.Add((source, writtenList));
                }
            }
            else
            {
                // All succeeded
                successfulChanges.AddRange(sourceChanges);
                successfulSourceWrites.Add((source, sourceChanges));
            }
        }

        // 4. Handle rollback mode - attempt to revert successful writes on failure
        if (mode == TransactionMode.Rollback && failedChanges.Count > 0 && successfulSourceWrites.Count > 0)
        {
            var revertFailures = await TryRevertSuccessfulWritesAsync(
                successfulSourceWrites,
                cancellationToken);

            failedChanges.AddRange(revertFailures);

            // In rollback mode with failures, report no successful changes
            // (either they failed originally or were reverted)
            return new TransactionWriteResult(changesWithoutSource, failedChanges);
        }

        return new TransactionWriteResult(successfulChanges, failedChanges);
    }

    /// <summary>
    /// Attempts to revert successful source writes by writing the old values back.
    /// This is best-effort - revert failures are collected and reported.
    /// </summary>
    private static async Task<List<SourceWriteFailure>> TryRevertSuccessfulWritesAsync(
        List<(ISubjectSource Source, List<SubjectPropertyChange> Changes)> successfulWrites,
        CancellationToken cancellationToken)
    {
        var revertFailures = new List<SourceWriteFailure>();

        foreach (var (source, originalChanges) in successfulWrites)
        {
            // Create rollback changes with old/new values swapped
            var rollbackChanges = originalChanges
                .Select(c => SubjectPropertyChange.Create(
                    c.Property,
                    source: c.Source,
                    changedTimestamp: DateTimeOffset.UtcNow,
                    receivedTimestamp: null,
                    c.GetNewValue<object?>(),  // Current "new" becomes old
                    c.GetOldValue<object?>())) // Original "old" becomes new (revert target)
                .ToArray();

            var memory = new ReadOnlyMemory<SubjectPropertyChange>(rollbackChanges);
            var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken);

            if (result.Error is not null)
            {
                var rollbackException = new SourceWriteException(
                    source,
                    originalChanges,
                    new InvalidOperationException($"Failed to rollback changes to source {source.GetType().Name}", result.Error));

                foreach (var change in originalChanges)
                {
                    revertFailures.Add(new SourceWriteFailure(change, source, rollbackException));
                }
            }
        }

        return revertFailures;
    }

    /// <summary>
    /// Validates that the SingleWrite requirement is satisfied.
    /// Returns a SourceWriteFailure if validation fails, or null if validation passes.
    /// </summary>
    private static SourceWriteFailure? ValidateSingleWriteRequirement(
        Dictionary<ISubjectSource, List<SubjectPropertyChange>> changesBySource,
        List<SubjectPropertyChange> changesWithoutSource)
    {
        if (changesBySource.Count == 0)
        {
            // No source changes - requirement satisfied (only changes without source)
            return null;
        }

        if (changesBySource.Count > 1)
        {
            var error = new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction contains changes for {changesBySource.Count} sources, but only 1 is allowed.");
            var firstChange = changesBySource.First().Value.First();
            var firstSource = changesBySource.First().Key;
            return new SourceWriteFailure(firstChange, firstSource, error);
        }

        // Single source - check batch size
        var (source, sourceChanges) = changesBySource.Single();
        var batchSize = source.WriteBatchSize;

        if (batchSize > 0 && sourceChanges.Count > batchSize)
        {
            var error = new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction contains {sourceChanges.Count} changes for source '{source.GetType().Name}', " +
                $"but WriteBatchSize is {batchSize}. Reduce the number of changes or use a different transaction requirement.");
            return new SourceWriteFailure(sourceChanges.First(), source, error);
        }

        return null;
    }
}
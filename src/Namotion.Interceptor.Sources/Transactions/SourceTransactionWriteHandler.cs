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
            var validationError = ValidateSingleWriteRequirement(changesBySource);
            if (validationError != null)
            {
                return new TransactionWriteResult(changesWithoutSource, [validationError]);
            }
        }

        // 3. Write to each source, tracking successful writes for potential rollback
        var successfulChanges = new List<SubjectPropertyChange>(changesWithoutSource);
        var successfulSourceWrites = new List<(ISubjectSource Source, List<SubjectPropertyChange> Changes)>();
        var failures = new List<Exception>();

        foreach (var (source, sourceChanges) in changesBySource)
        {
            var memory = new ReadOnlyMemory<SubjectPropertyChange>(sourceChanges.ToArray());
            var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken);

            // Track successful changes (may be partial or complete)
            var writtenList = result.SuccessfulChanges.ToArray().ToList();
            if (writtenList.Count > 0)
            {
                successfulChanges.AddRange(writtenList);
                successfulSourceWrites.Add((source, writtenList));
            }

            // Record any failure
            if (result.Error is not null)
            {
                failures.Add(new SourceWriteException(source, sourceChanges, result.Error));
            }
        }

        // 4. Handle rollback mode - attempt to revert successful writes on failure
        if (mode == TransactionMode.Rollback && failures.Count > 0 && successfulSourceWrites.Count > 0)
        {
            var revertFailures = await TryRevertSuccessfulWritesAsync(
                successfulSourceWrites,
                cancellationToken);

            failures.AddRange(revertFailures);

            // In rollback mode with failures, report no successful changes
            // (either they failed originally or were reverted)
            return new TransactionWriteResult(changesWithoutSource, failures);
        }

        return new TransactionWriteResult(successfulChanges, failures);
    }

    /// <summary>
    /// Attempts to revert successful source writes by writing the old values back.
    /// This is best-effort - revert failures are collected and reported.
    /// </summary>
    private static async Task<List<Exception>> TryRevertSuccessfulWritesAsync(
        List<(ISubjectSource Source, List<SubjectPropertyChange> Changes)> successfulWrites,
        CancellationToken cancellationToken)
    {
        var revertFailures = new List<Exception>();

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
                revertFailures.Add(new SourceWriteException(
                    source,
                    originalChanges,
                    new InvalidOperationException($"Failed to rollback changes to source {source.GetType().Name}", result.Error)));
            }
        }

        return revertFailures;
    }

    /// <summary>
    /// Validates that the SingleWrite requirement is satisfied.
    /// Returns an InvalidOperationException if validation fails, or null if validation passes.
    /// </summary>
    private static InvalidOperationException? ValidateSingleWriteRequirement(
        Dictionary<ISubjectSource, List<SubjectPropertyChange>> changesBySource)
    {
        if (changesBySource.Count == 0)
        {
            // No source changes - requirement satisfied (only changes without source)
            return null;
        }

        if (changesBySource.Count > 1)
        {
            return new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction contains changes for {changesBySource.Count} sources, but only 1 is allowed.");
        }

        // Single source - check batch size
        var (source, sourceChanges) = changesBySource.Single();
        var batchSize = source.WriteBatchSize;

        if (batchSize > 0 && sourceChanges.Count > batchSize)
        {
            return new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction contains {sourceChanges.Count} changes for source '{source.GetType().Name}', " +
                $"but WriteBatchSize is {batchSize}. Reduce the number of changes or use a different transaction requirement.");
        }

        return null;
    }
}
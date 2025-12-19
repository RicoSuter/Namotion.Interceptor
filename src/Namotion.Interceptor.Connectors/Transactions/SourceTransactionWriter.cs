using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Transactions;

/// <summary>
/// Handler that writes transaction changes to their associated external sources.
/// Supports different transaction modes including rollback on failure.
/// </summary>
internal sealed class SourceTransactionWriter : ITransactionWriter
{
    public async Task<TransactionWriteResult> WriteChangesAsync(
        IReadOnlyList<SubjectPropertyChange> changes,
        TransactionMode mode,
        TransactionRequirement requirement,
        CancellationToken cancellationToken)
    {
        // 1. Group changes by source (NullSource.Instance = changes without source, always successful)
        var changesBySource = changes
            .GroupBy(c => c.Property.TryGetSource(out var s) ? s : NullSource.Instance)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 2. Validate SingleWrite requirement (only one source, one batch; changes without source are always fine)
        if (requirement == TransactionRequirement.SingleWrite)
        {
            var validationError = ValidateSingleWriteRequirement(changesBySource);
            if (validationError != null)
            {
                return new TransactionWriteResult(
                    changesBySource.GetValueOrDefault(NullSource.Instance, []),
                    [validationError]);
            }
        }

        // 3. Write to each source
        var (successfulChangesBySource, failedChanges) = await WriteToSourcesAsync(changesBySource, cancellationToken);

        // 4. Handle rollback mode - attempt to revert successful writes on failure
        if (mode == TransactionMode.Rollback && failedChanges.Count > 0)
        {
            var successfulSourceWrites = successfulChangesBySource
                .Where(kvp => kvp.Key != NullSource.Instance)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (successfulSourceWrites.Count > 0)
            {
                var revertFailures = await TryRevertSuccessfulWritesAsync(successfulSourceWrites, cancellationToken);
                failedChanges.AddRange(revertFailures);
                
                // In rollback mode with failures, only changes without a source are successful
                return new TransactionWriteResult(
                    successfulChangesBySource.GetValueOrDefault(NullSource.Instance, []),
                    failedChanges);
            }
        }

        return new TransactionWriteResult(
            successfulChangesBySource.Values.SelectMany(c => c).ToList(),
            failedChanges);
    }

    /// <summary>
    /// Writes changes to their respective sources and collects results.
    /// </summary>
    private static async Task<(Dictionary<ISubjectSource, List<SubjectPropertyChange>> Successful, List<SourceWriteFailure> Failed)>
        WriteToSourcesAsync(
            Dictionary<ISubjectSource, List<SubjectPropertyChange>> changesBySource,
            CancellationToken cancellationToken)
    {
        var successfulChangesBySource = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>();
        var failedChanges = new List<SourceWriteFailure>();

        foreach (var (source, sourceChanges) in changesBySource)
        {
            if (source == NullSource.Instance)
            {
                successfulChangesBySource[source] = sourceChanges;
                continue;
            }

            var (successful, failed) = await TryWriteToSourceAsync(source, sourceChanges, cancellationToken);
            if (successful.Count > 0)
            {
                successfulChangesBySource[source] = successful;
            }
            failedChanges.AddRange(failed);
        }

        return (successfulChangesBySource, failedChanges);
    }

    /// <summary>
    /// Attempts to write changes to a single source, returning successful and failed changes.
    /// </summary>
    private static async Task<(List<SubjectPropertyChange> Successful, List<SourceWriteFailure> Failed)>
        TryWriteToSourceAsync(
            ISubjectSource source,
            List<SubjectPropertyChange> sourceChanges,
            CancellationToken cancellationToken)
    {
        var memory = new ReadOnlyMemory<SubjectPropertyChange>(sourceChanges.ToArray());
        var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken);

        if (result.Error is not null)
        {
            var failedList = result.FailedChanges.Length > 0
                ? result.FailedChanges.ToArray()
                : sourceChanges.ToArray();

            var failedChanges = new List<SourceWriteFailure>();
            foreach (var failedChange in failedList)
            {
                failedChanges.Add(new SourceWriteFailure(
                    failedChange,
                    source,
                    new SourceTransactionWriteException(source, [failedChange], result.Error)));
            }

            var failedSet = new HashSet<SubjectPropertyChange>(failedList);
            var writtenList = sourceChanges.Where(c => !failedSet.Contains(c)).ToList();

            return (writtenList, failedChanges);
        }

        return (sourceChanges, []);
    }

    /// <summary>
    /// Attempts to revert successful source writes by writing the old values back.
    /// </summary>
    private static async Task<List<SourceWriteFailure>> TryRevertSuccessfulWritesAsync(
        Dictionary<ISubjectSource, List<SubjectPropertyChange>> successfulWrites,
        CancellationToken cancellationToken)
    {
        var revertFailures = new List<SourceWriteFailure>();
        foreach (var (source, originalChanges) in successfulWrites)
        {
            var rollbackChanges = originalChanges
                .Select(c => SubjectPropertyChange.Create(
                    c.Property,
                    source: c.Source,
                    changedTimestamp: DateTimeOffset.UtcNow,
                    receivedTimestamp: null,
                    c.GetNewValue<object?>(),
                    c.GetOldValue<object?>()))
                .ToArray();

            var memory = new ReadOnlyMemory<SubjectPropertyChange>(rollbackChanges);
            var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken);

            if (result.Error is not null)
            {
                var rollbackException = new SourceTransactionWriteException(
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
    /// Only one source with one batch is allowed; changes without source are always fine.
    /// </summary>
    private static SourceWriteFailure? ValidateSingleWriteRequirement(
        Dictionary<ISubjectSource, List<SubjectPropertyChange>> changesBySource)
    {
        var sourcesWithChanges = changesBySource
            .Where(kvp => kvp.Key != NullSource.Instance)
            .ToList();

        if (sourcesWithChanges.Count == 0)
            return null;

        if (sourcesWithChanges.Count > 1)
        {
            var exception = new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction contains changes for {sourcesWithChanges.Count} sources, but only 1 is allowed.");

            return new SourceWriteFailure(
                sourcesWithChanges.First().Value.First(),
                sourcesWithChanges.First().Key,
                exception);
        }

        var (source, sourceChanges) = (sourcesWithChanges[0].Key, sourcesWithChanges[0].Value);
        var batchSize = source.WriteBatchSize;

        if (batchSize > 0 && sourceChanges.Count > batchSize)
        {
            var exception = new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction contains {sourceChanges.Count} changes for source '{source.GetType().Name}', " +
                $"but WriteBatchSize is {batchSize}.");

            return new SourceWriteFailure(sourceChanges.First(), source, exception);
        }

        return null;
    }

    /// <summary>
    /// Sentinel class representing "no source" for dictionary keys.
    /// </summary>
    private sealed class NullSource : ISubjectSource
    {
        public static readonly NullSource Instance = new();
        private NullSource() { }

        public IInterceptorSubject RootSubject => throw new NotSupportedException();

        public int WriteBatchSize => 0;

        public Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}

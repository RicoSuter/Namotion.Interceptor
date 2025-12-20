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
        TransactionFailureHandling failureHandling,
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
                var allChangesWithSource = changesBySource
                    .Where(kvp => kvp.Key != NullSource.Instance)
                    .SelectMany(kvp => kvp.Value)
                    .ToList();

                return new TransactionWriteResult(
                    changesBySource.GetValueOrDefault(NullSource.Instance, []),
                    allChangesWithSource,
                    [validationError]);
            }
        }

        // 3. Write to each source
        var (successfulChangesBySource, failedChanges, errors) = await WriteChangesToSourcesAsync(changesBySource, cancellationToken);

        // 4. Handle rollback mode - attempt to revert successful writes on failure
        if (failureHandling == TransactionFailureHandling.Rollback && failedChanges.Count > 0)
        {
            var successfulSourceWrites = successfulChangesBySource
                .Where(kvp => kvp.Key != NullSource.Instance)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (successfulSourceWrites.Count > 0)
            {
                var (revertFailedChanges, revertErrors) = await TryRevertSuccessfulWritesAsync(successfulSourceWrites, cancellationToken);
                failedChanges.AddRange(revertFailedChanges);
                errors.AddRange(revertErrors);

                // In rollback mode with failures, only changes without a source are successful
                return new TransactionWriteResult(
                    successfulChangesBySource.GetValueOrDefault(NullSource.Instance, []),
                    failedChanges,
                    errors);
            }
        }

        return new TransactionWriteResult(
            successfulChangesBySource.Values.SelectMany(c => c).ToList(),
            failedChanges,
            errors);
    }

    /// <summary>
    /// Writes changes to their respective sources in parallel and collects results.
    /// </summary>
    private static async Task<(Dictionary<ISubjectSource, List<SubjectPropertyChange>> Successful, List<SubjectPropertyChange> Failed, List<Exception> Errors)>
        WriteChangesToSourcesAsync(
            Dictionary<ISubjectSource, List<SubjectPropertyChange>> changesBySource,
            CancellationToken cancellationToken)
    {
        var successfulChangesBySource = new Dictionary<ISubjectSource, List<SubjectPropertyChange>>();
        var failedChanges = new List<SubjectPropertyChange>();
        var errors = new List<Exception>();

        // Separate null source (always successful, no async work)
        if (changesBySource.TryGetValue(NullSource.Instance, out var nullSourceChanges))
        {
            successfulChangesBySource[NullSource.Instance] = nullSourceChanges;
        }

        // Get real sources that need actual writes
        var realSources = changesBySource
            .Where(kvp => kvp.Key != NullSource.Instance)
            .ToList();

        if (realSources.Count == 0)
        {
            return (successfulChangesBySource, failedChanges, errors);
        }

        // Write to all real sources in parallel
        var writeTasks = realSources.Select(async kvp =>
        {
            var (source, sourceChanges) = (kvp.Key, kvp.Value);
            var result = await TryWriteChangesToSourceAsync(source, sourceChanges, cancellationToken);
            return (source, result);
        });

        var results = await Task.WhenAll(writeTasks);
        foreach (var (source, (successful, failed, error)) in results)
        {
            if (successful.Count > 0)
            {
                successfulChangesBySource[source] = successful;
            }
            failedChanges.AddRange(failed);
            if (error != null)
            {
                errors.Add(error);
            }
        }

        return (successfulChangesBySource, failedChanges, errors);
    }

    /// <summary>
    /// Attempts to write changes to a single source, returning successful and failed changes.
    /// </summary>
    private static async Task<(List<SubjectPropertyChange> Successful, List<SubjectPropertyChange> Failed, Exception? Error)>
        TryWriteChangesToSourceAsync(
            ISubjectSource source,
            List<SubjectPropertyChange> sourceChanges,
            CancellationToken cancellationToken)
    {
        var memory = new ReadOnlyMemory<SubjectPropertyChange>(sourceChanges.ToArray());

        var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken);
        if (result.Error is not null)
        {
            var failedSet = new HashSet<SubjectPropertyChange>(result.FailedChanges);
            var writtenList = sourceChanges.Where(c => !failedSet.Contains(c)).ToList();
            var error = new SourceTransactionWriteException(source, [..result.FailedChanges], result.Error);

            return (writtenList, [..result.FailedChanges], error);
        }

        return (sourceChanges, [], null);
    }

    /// <summary>
    /// Attempts to revert successful source writes by writing the old values back.
    /// Rollback is done in reverse order to properly undo changes.
    /// </summary>
    private static async Task<(List<SubjectPropertyChange> FailedChanges, List<Exception> Errors)> TryRevertSuccessfulWritesAsync(
        Dictionary<ISubjectSource, List<SubjectPropertyChange>> successfulWrites,
        CancellationToken cancellationToken)
    {
        var failedChanges = new List<SubjectPropertyChange>();
        var errors = new List<Exception>();

        foreach (var (source, originalChanges) in successfulWrites)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reverse order for proper rollback (undo in opposite order of apply)
            var rollbackChanges = originalChanges
                .AsEnumerable()
                .Reverse()
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

                failedChanges.AddRange(originalChanges);
                errors.Add(rollbackException);
            }
        }

        return (failedChanges, errors);
    }

    /// <summary>
    /// Validates that the SingleWrite requirement is satisfied.
    /// Only one source with one batch is allowed; changes without source are always fine.
    /// </summary>
    private static Exception? ValidateSingleWriteRequirement(
        Dictionary<ISubjectSource, List<SubjectPropertyChange>> changesBySource)
    {
        var sourcesWithChanges = changesBySource
            .Where(kvp => kvp.Key != NullSource.Instance)
            .ToList();

        if (sourcesWithChanges.Count == 0)
            return null;

        if (sourcesWithChanges.Count > 1)
        {
            var sourceNames = string.Join(", ", sourcesWithChanges.Select(kvp => kvp.Key.GetType().Name));
            return new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction spans {sourcesWithChanges.Count} sources ({sourceNames}), but only 1 is allowed.");
        }

        var (source, sourceChanges) = (sourcesWithChanges[0].Key, sourcesWithChanges[0].Value);
        var batchSize = source.WriteBatchSize;

        if (batchSize > 0 && sourceChanges.Count > batchSize)
        {
            return new InvalidOperationException(
                $"SingleWrite requirement violated: Transaction contains {sourceChanges.Count} changes for source '{source.GetType().Name}', " +
                $"but WriteBatchSize is {batchSize}.");
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

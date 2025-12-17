using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public static class SubjectSourceExtensions
{
    private static readonly ConditionalWeakTable<ISubjectSource, SourceWriteLock> WriteLocks = new();

    /// <summary>
    /// Writes changes to the source in batches, respecting the source's maximum batch size.
    /// Returns a <see cref="WriteResult"/> containing which changes failed.
    /// Never throws for write failures - errors are reported in the result.
    /// Zero-allocation on success path.
    /// </summary>
    /// <remarks>
    /// This method automatically synchronizes write operations unless the source implements
    /// <see cref="ISupportsConcurrentWrites"/>. Callers should always use this method
    /// instead of calling <see cref="ISubjectSource.WriteChangesAsync"/> directly.
    /// </remarks>
    /// <returns>A <see cref="WriteResult"/> containing failed changes and any error.</returns>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<WriteResult> WriteChangesInBatchesAsync(
        this ISubjectSource source,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        var count = changes.Length;
        if (count == 0)
        {
            return WriteResult.Success();
        }

        // Skip synchronization for sources that handle their own concurrency
        if (source is ISupportsConcurrentWrites)
        {
            return await WriteChangesInBatchesCoreAsync(source, changes, cancellationToken).ConfigureAwait(false);
        }

        var writeLock = WriteLocks.GetValue(source, static _ => new SourceWriteLock());
        try
        {
            await writeLock.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // Return failure instead of throwing - let caller handle cancellation uniformly
            return WriteResult.Failure(changes, ex);
        }

        try
        {
            return await WriteChangesInBatchesCoreAsync(source, changes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Semaphore.Release();
        }
    }

    private static async ValueTask<WriteResult> WriteChangesInBatchesCoreAsync(
        ISubjectSource source,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        var count = changes.Length;
        var batchSize = source.WriteBatchSize;

        if (batchSize <= 0 || count <= batchSize)
        {
            // Single batch - delegate directly to source (zero allocation)
            return await source.WriteChangesAsync(changes, cancellationToken).ConfigureAwait(false);
        }

        // Multi-batch: process sequentially, stop on first failure
        for (var i = 0; i < count; i += batchSize)
        {
            var currentBatchSize = Math.Min(batchSize, count - i);
            var batch = changes.Slice(i, currentBatchSize);

            var batchResult = await source.WriteChangesAsync(batch, cancellationToken).ConfigureAwait(false);
            if (batchResult.Error is not null)
            {
                // This batch failed - return remaining changes as failed
                // Include any specific failures from this batch plus all unprocessed batches
                var remainingStart = i + currentBatchSize - batchResult.FailedChanges.Length;
                if (batchResult.FailedChanges.Length == 0)
                {
                    // Complete batch failure - all remaining changes failed
                    remainingStart = i;
                }

                var failedChanges = changes.Slice(remainingStart);
                return WriteResult.PartialFailure(failedChanges, batchResult.Error);
            }
        }

        // All batches succeeded (zero allocation)
        return WriteResult.Success();
    }

    /// <summary>
    /// Internal lock holder for per-source write synchronization.
    /// </summary>
    internal sealed class SourceWriteLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

public static class SubjectSourceExtensions
{
    // TODO: ConditionalWeakTable pattern means SemaphoreSlim is not explicitly disposed when source is GC'd.
    // This relies on SemaphoreSlim's finalizer for cleanup. For long-lived sources (typical), this is acceptable.
    // If short-lived sources become common, consider having sources own their write lock via IDisposable.
    private static readonly ConditionalWeakTable<ISubjectSource, SourceWriteLock> WriteLocks = new();

    /// <summary>
    /// Writes changes to the source in batches, respecting the source's maximum batch size.
    /// Returns a <see cref="WriteResult"/> containing which changes failed. A failing batch does not
    /// stop the ones behind it: every batch is attempted and their failures are reported together,
    /// with the first error, joined with a later throw into an <see cref="AggregateException"/> when one
    /// arrives. Never throws for write failures, errors are reported in the result.
    /// <para>
    /// Batches are only independent of each other because <paramref name="changes"/> carries at most one
    /// change per property. With two, a failure of the batch holding the older one while the batch holding
    /// the newer one succeeds leaves only the older to be retried, and the source settles on it for good.
    /// </para>
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
            return WriteResult.Success;
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
        // Allocated only once a batch has actually failed, so an all-success flush allocates nothing.
        List<SubjectPropertyChange>? failedChanges = null;
        Exception? firstError = null;
        var batchStart = 0;
        try
        {
            var count = changes.Length;
            var batchSize = source.WriteBatchSize;

            if (batchSize <= 0 || count <= batchSize)
            {
                // Single batch - delegate directly to source (zero allocation on success)
                var result = await source.WriteChangesAsync(changes, cancellationToken).ConfigureAwait(false);

                // Normalize the unenumerated-failure shorthand once, at the choke point all callers use.
                return result.Error is not null && result.FailedChanges.IsEmpty
                    ? WriteResult.Failure(changes, result.Error)
                    : result;
            }

            // Multi-batch: every batch is attempted and their failures accumulate into one result. One
            // change the source refuses would otherwise starve everything queued behind it, since the
            // batches after it would be condemned unattempted on every retry for as long as it fails.
            for (; batchStart < count; batchStart += batchSize)
            {
                var currentBatchSize = Math.Min(batchSize, count - batchStart);
                var batch = changes.Slice(batchStart, currentBatchSize);

                var batchResult = await source.WriteChangesAsync(batch, cancellationToken).ConfigureAwait(false);
                if (batchResult.Error is null)
                {
                    continue;
                }

                firstError ??= batchResult.Error;
                failedChanges ??= [];

                // The batch's failed changes, or the whole batch when the source left them unenumerated.
                failedChanges.AddRange(batchResult.FailedChanges.IsEmpty
                    ? batch.Span
                    : batchResult.FailedChanges.AsSpan());

                if (cancellationToken.IsCancellationRequested)
                {
                    // Pushing the rest at a source that is going away gains nothing, and a batch that is
                    // never attempted is unconfirmed.
                    failedChanges.AddRange(changes.Span[(batchStart + currentBatchSize)..]);
                    break;
                }
            }

            return firstError is null
                ? WriteResult.Success
                : CreatePartialFailure(failedChanges!, firstError);
        }
        catch (Exception ex)
        {
            // The throwing batch's outcome is unknown and the remainder was never attempted, so both are
            // unconfirmed. Batches attempted before it keep the verdict they already got: a slice from
            // here would condemn those that succeeded after an earlier batch failed, and a change
            // reported failed after reaching the source is never reverted by a source transaction.
            var unconfirmed = changes.Slice(batchStart);
            if (failedChanges is null)
            {
                return batchStart == 0
                    ? WriteResult.Failure(changes, ex)
                    : WriteResult.PartialFailure(unconfirmed, ex);
            }

            // Consumers log only the reported error, so reporting the first one alone would drop the throw
            // with its stack. AggregateException.ToString renders both, which Exception.Data does not.
            // firstError is set together with failedChanges, so it is non-null.
            failedChanges.AddRange(unconfirmed.Span);
            return CreatePartialFailure(failedChanges, new AggregateException(firstError!, ex));
        }
    }

    private static WriteResult CreatePartialFailure(List<SubjectPropertyChange> failedChanges, Exception error)
    {
        // The array never escapes, so the ImmutableArray takes ownership without a second copy.
        return WriteResult.PartialFailure(
            ImmutableCollectionsMarshal.AsImmutableArray(failedChanges.ToArray()), error);
    }

    /// <summary>
    /// Internal lock holder for per-source write synchronization.
    /// </summary>
    internal sealed class SourceWriteLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
    }
}

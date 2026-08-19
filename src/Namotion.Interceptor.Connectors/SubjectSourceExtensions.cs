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
    /// Returns a <see cref="WriteResult"/> containing which changes failed. A batch that names the changes
    /// it refused does not stop the ones behind it: they are attempted too and the failures are reported
    /// together, with every failing batch's error carried and aggregated when more than one batch fails,
    /// so a later failure cannot be masked by an earlier one. A batch that fails without naming any change
    /// stops the flush, and it and the remainder are reported failed. Never throws for write failures,
    /// errors are reported in the result.
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
        List<SubjectPropertyChange>? refusedChanges = null;
        List<Exception>? errors = null;
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

            for (; batchStart < count; batchStart += batchSize)
            {
                var currentBatchSize = Math.Min(batchSize, count - batchStart);
                var batch = changes.Slice(batchStart, currentBatchSize);

                var batchResult = await source.WriteChangesAsync(batch, cancellationToken).ConfigureAwait(false);
                if (batchResult.Error is null)
                {
                    continue;
                }

                // Every failing batch's error is kept: batches fail independently, and reporting only
                // the first would let a node refusal in an early batch mask the disconnect a later
                // batch died of, which is the one error an operator can act on.
                (errors ??= []).Add(batchResult.Error);
                failedChanges ??= [];

                if (batchResult.FailedChanges.IsEmpty)
                {
                    // Naming no change means the source never answered per item, so the call itself
                    // failed: a timeout, a dropped channel, a faulted session. Each batch behind it
                    // would buy the same verdict at the price of another transport timeout, with the
                    // write lock held for all of them, so the rest of the flush is left unattempted
                    // and reported unconfirmed.
                    failedChanges.AddRange(changes.Span[batchStart..]);
                    break;
                }

                // An enumerated refusal is an answer about named changes, and says nothing about the
                // batches behind it. One change the source refuses would otherwise starve everything
                // queued behind it, condemned unattempted on every retry for as long as it fails.
                failedChanges.AddRange(batchResult.FailedChanges.AsSpan());

                if (!batchResult.RefusedUntilReconnect.IsDefaultOrEmpty)
                {
                    (refusedChanges ??= []).AddRange(batchResult.RefusedUntilReconnect.AsSpan());
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    // An unattempted batch is unconfirmed, and a source that is going away will not take it.
                    failedChanges.AddRange(changes.Span[(batchStart + currentBatchSize)..]);
                    break;
                }
            }

            return errors is null
                ? WriteResult.Success
                : CreateFailure(failedChanges!, CombineErrors(errors), refusedChanges);
        }
        catch (Exception ex)
        {
            // The throwing batch's outcome is unknown and the remainder was never attempted, so both are
            // unconfirmed. Batches attempted before it keep the verdict they already got: slicing from
            // here would condemn those that succeeded after an earlier batch failed.
            var unconfirmed = changes.Slice(batchStart);
            if (failedChanges is null)
            {
                return WriteResult.Failure(unconfirmed, ex);
            }

            // Consumers log only the reported error, so reporting the recorded ones alone would drop the
            // throw with its stack. errors is set together with failedChanges, so it is non-null.
            failedChanges.AddRange(unconfirmed.Span);
            errors!.Add(ex);
            return CreateFailure(failedChanges, CombineErrors(errors), refusedChanges);
        }
    }

    private static Exception CombineErrors(List<Exception> errors)
    {
        // An AggregateException renders every inner exception with its stack, so nothing is lost when
        // batches fail differently; a lone error is reported as itself to keep the common case readable.
        return errors.Count == 1 ? errors[0] : new AggregateException(errors);
    }

    private static WriteResult CreateFailure(
        List<SubjectPropertyChange> failedChanges, Exception error, List<SubjectPropertyChange>? refusedChanges)
    {
        // The arrays never escape, so the ImmutableArrays take ownership without a second copy.
        var result = WriteResult.Failure(
            ImmutableCollectionsMarshal.AsImmutableArray(failedChanges.ToArray()), error);

        return refusedChanges is null
            ? result
            : result.WithRefusedUntilReconnect(
                ImmutableCollectionsMarshal.AsImmutableArray(refusedChanges.ToArray()));
    }

    /// <summary>
    /// Internal lock holder for per-source write synchronization.
    /// </summary>
    internal sealed class SourceWriteLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
    }
}

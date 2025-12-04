using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public static class SubjectSourceExtensions
{
    /// <summary>
    /// Writes changes to the source in batches, respecting the source's maximum batch size.
    /// Returns a <see cref="WriteResult"/> containing which changes succeeded.
    /// Never throws for write failures - errors are reported in the result.
    /// Zero-allocation on success path (returns slice of original memory).
    /// </summary>
    /// <returns>A <see cref="WriteResult"/> containing successful changes and any error.</returns>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<WriteResult> WriteChangesInBatchesAsync(
        this ISubjectSource source,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        var count = changes.Length;
        if (count == 0)
        {
            return WriteResult.Success(ReadOnlyMemory<SubjectPropertyChange>.Empty);
        }

        var batchSize = source.WriteBatchSize;
        if (batchSize <= 0 || count <= batchSize)
        {
            // Single batch - delegate directly to source (zero allocation)
            return await source.WriteChangesAsync(changes, cancellationToken).ConfigureAwait(false);
        }

        // Multi-batch: track cumulative success count (zero allocation for success path)
        var successfulCount = 0;

        for (var i = 0; i < count; i += batchSize)
        {
            var currentBatchSize = Math.Min(batchSize, count - i);
            var batch = changes.Slice(i, currentBatchSize);

            var batchResult = await source.WriteChangesAsync(batch, cancellationToken).ConfigureAwait(false);

            // Track successful count from this batch
            successfulCount += batchResult.SuccessfulChanges.Length;

            // If this batch had an error, return partial success
            // Use slice of original memory for previously completed batches
            // plus the partial success from current batch
            if (batchResult.Error is not null)
            {
                // Optimization: if all successes are from complete batches, just slice
                if (batchResult.SuccessfulChanges.Length == 0)
                {
                    // No partial success in failed batch - just return slice of completed batches
                    var completedCount = successfulCount;
                    return WriteResult.PartialSuccess(changes.Slice(0, completedCount), batchResult.Error);
                }
                else
                {
                    // Partial success in failed batch - need to combine (rare case, allocates)
                    var combined = CombineSuccessfulChanges(changes, i, batchResult.SuccessfulChanges);
                    return WriteResult.PartialSuccess(combined, batchResult.Error);
                }
            }
        }

        // All batches succeeded - return original memory slice (zero allocation)
        return WriteResult.Success(changes);
    }

    /// <summary>
    /// Combines previously completed batch changes with partial success from current batch.
    /// Only called in the rare case of partial success within a batch.
    /// </summary>
    private static SubjectPropertyChange[] CombineSuccessfulChanges(
        ReadOnlyMemory<SubjectPropertyChange> allChanges,
        int completedBatchesEndIndex,
        ReadOnlyMemory<SubjectPropertyChange> partialBatchSuccess)
    {
        var combined = new SubjectPropertyChange[completedBatchesEndIndex + partialBatchSuccess.Length];
        allChanges.Slice(0, completedBatchesEndIndex).CopyTo(combined);
        partialBatchSuccess.CopyTo(combined.AsMemory(completedBatchesEndIndex));
        return combined;
    }
}


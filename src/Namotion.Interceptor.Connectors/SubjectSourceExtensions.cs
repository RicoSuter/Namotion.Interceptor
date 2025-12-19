using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

public static class SubjectSourceExtensions
{
    /// <summary>
    /// Writes changes to the source in batches, respecting the source's maximum batch size.
    /// Returns the number of items successfully written. If an exception occurs, only items
    /// up to (but not including) the failed batch are counted as successful.
    /// </summary>
    /// <returns>The number of items successfully written to the source.</returns>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<int> WriteChangesInBatchesAsync(this ISubjectSource source, ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var count = changes.Length;
        if (count == 0)
        {
            return 0;
        }

        var batchSize = source.WriteBatchSize;
        if (batchSize <= 0 || count <= batchSize)
        {
            await source.WriteChangesAsync(changes, cancellationToken).ConfigureAwait(false);
            return count;
        }

        var writtenCount = 0;

        // Zero-allocation batching using Memory.Slice()
        for (var i = 0; i < count; i += batchSize)
        {
            var currentBatchSize = Math.Min(batchSize, count - i);
            var batch = changes.Slice(i, currentBatchSize);
            await source.WriteChangesAsync(batch, cancellationToken).ConfigureAwait(false);
            writtenCount += currentBatchSize;
        }

        return writtenCount;
    }
}

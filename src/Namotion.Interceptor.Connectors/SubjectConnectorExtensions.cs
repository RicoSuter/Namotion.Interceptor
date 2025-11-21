using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

public static class SubjectConnectorExtensions
{
    /// <summary>
    /// Writes changes to the connector in batches, respecting the connector's maximum batch size.
    /// Returns the number of items successfully written. If an exception occurs, only items
    /// up to (but not including) the failed batch are counted as successful.
    /// </summary>
    /// <returns>The number of items successfully written to the connector.</returns>
    public static async ValueTask<int> WriteToSourceInBatchesAsync(this ISubjectConnector connector, ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var count = changes.Length;
        if (count == 0)
        {
            return 0;
        }

        var batchSize = connector.WriteBatchSize;
        if (batchSize <= 0 || count <= batchSize)
        {
            await connector.WriteChangesAsync(changes, cancellationToken).ConfigureAwait(false);
            return count;
        }

        var writtenCount = 0;

        // Zero-allocation batching using Memory.Slice()
        for (var i = 0; i < count; i += batchSize)
        {
            var currentBatchSize = Math.Min(batchSize, count - i);
            var batch = changes.Slice(i, currentBatchSize);
            await connector.WriteChangesAsync(batch, cancellationToken).ConfigureAwait(false);
            writtenCount += currentBatchSize;
        }

        return writtenCount;
    }
}

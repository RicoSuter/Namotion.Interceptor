using Namotion.Interceptor.Sources;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Exception thrown when an OPC UA write operation has failures.
/// Contains summary information about transient vs permanent failures.
/// Detailed per-node success information is provided in <see cref="WriteResult"/>.
/// </summary>
public sealed class OpcUaWriteException : InvalidOperationException
{
    /// <summary>
    /// Gets the number of transient failures (connectivity, timeouts - may be retried).
    /// </summary>
    public int TransientFailureCount { get; }

    /// <summary>
    /// Gets the number of permanent failures (invalid nodes, access denied - should not be retried).
    /// </summary>
    public int PermanentFailureCount { get; }

    /// <summary>
    /// Gets the total number of writes attempted.
    /// </summary>
    public int TotalWrites { get; }

    /// <summary>
    /// Gets the total number of failures.
    /// </summary>
    public int TotalFailureCount => TransientFailureCount + PermanentFailureCount;

    public OpcUaWriteException(int transientFailureCount, int permanentFailureCount, int totalWrites)
        : base($"OPC UA write failed: {transientFailureCount} transient failures, {permanentFailureCount} permanent failures out of {totalWrites} writes.")
    {
        TransientFailureCount = transientFailureCount;
        PermanentFailureCount = permanentFailureCount;
        TotalWrites = totalWrites;
    }
}

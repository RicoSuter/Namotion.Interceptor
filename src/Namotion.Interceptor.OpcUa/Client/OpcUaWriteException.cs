using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Exception thrown when an OPC UA write operation has failures.
/// Detailed per-node failure information is provided in <see cref="WriteResult"/>.
/// </summary>
public sealed class OpcUaWriteException : InvalidOperationException
{
    /// <summary>
    /// Gets the total number of writes attempted.
    /// </summary>
    public int TotalWrites { get; }

    public OpcUaWriteException(int failedWrites, int totalWrites)
        : base($"OPC UA write failed: {failedWrites} of {totalWrites} writes failed.")
    {
        TotalWrites = totalWrites;
    }
}

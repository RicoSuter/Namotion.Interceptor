namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Marker interface for sources that support concurrent write operations.
/// Sources implementing this interface opt-out of automatic synchronization
/// in <see cref="SubjectSourceExtensions.WriteChangesInBatchesAsync"/>.
/// </summary>
/// <remarks>
/// Most sources should NOT implement this interface. Only implement it if:
/// <list type="bullet">
/// <item>Your underlying protocol supports concurrent writes (e.g., HTTP/2 multiplexing)</item>
/// <item>You have custom synchronization requirements</item>
/// </list>
/// </remarks>
public interface ISupportsConcurrentWrites;

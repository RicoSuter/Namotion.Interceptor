namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Searches the local network over SSDP.
/// </summary>
public interface ISsdpSearch
{
    /// <summary>
    /// Searches every IPv4 adapter for <paramref name="searchTarget"/> and returns the answers for exactly that target
    /// that arrive within a few seconds. Returns no answers instead of throwing when no adapter can search.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken);
}

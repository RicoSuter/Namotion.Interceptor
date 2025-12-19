namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Base interface for components that connect subjects to external systems.
/// Implemented by sources (inbound sync) and optionally by servers (outbound exposure).
/// </summary>
public interface ISubjectConnector
{
    /// <summary>
    /// Gets the root subject being connected to an external system.
    /// </summary>
    IInterceptorSubject RootSubject { get; }
}

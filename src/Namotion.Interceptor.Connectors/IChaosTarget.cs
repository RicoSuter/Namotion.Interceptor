namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Interface for connectors that support chaos injection for resilience testing.
/// Separates testing concerns from the production <see cref="ISubjectConnector"/> interface.
/// </summary>
public interface IChaosTarget
{
    /// <summary>
    /// Hard kill: stops the connector entirely. The connector's background loop should automatically restart.
    /// </summary>
    Task KillAsync();

    /// <summary>
    /// Soft kill: breaks the transport connection without stopping the connector.
    /// Lets the SDK's built-in reconnection logic detect the failure and recover.
    /// </summary>
    Task DisconnectAsync();
}

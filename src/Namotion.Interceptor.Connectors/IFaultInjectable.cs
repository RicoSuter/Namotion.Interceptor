namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Defines the type of fault to inject for resilience testing.
/// </summary>
public enum FaultType
{
    /// <summary>
    /// Hard kill: stops the connector entirely. The connector's background loop should automatically restart.
    /// </summary>
    Kill,

    /// <summary>
    /// Soft kill: breaks the transport connection without stopping the connector.
    /// Lets the SDK's built-in reconnection logic detect the failure and recover.
    /// </summary>
    Disconnect
}

/// <summary>
/// Interface for connectors that support fault injection for resilience testing.
/// Separates testing concerns from the production <see cref="ISubjectConnector"/> interface.
/// </summary>
public interface IFaultInjectable
{
    /// <summary>
    /// Injects a fault of the specified type into the connector.
    /// </summary>
    /// <param name="faultType">The type of fault to inject.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken = default);
}

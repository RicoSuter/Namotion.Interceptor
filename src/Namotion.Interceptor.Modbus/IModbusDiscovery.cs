namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Implemented by a source's root subject to inspect the device on every connect (first connect and each
/// reconnect), before the connector resolves its register bindings. Typical uses are firmware gating and
/// runtime discovery.
/// </summary>
public interface IModbusDiscovery
{
    /// <summary>
    /// Called after the connection is established. The context is invalid once the returned task completes.
    /// Throwing fails the connect attempt, which is retried.
    /// </summary>
    Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken);
}

using Namotion.Interceptor.Modbus.Transport;

namespace Namotion.Interceptor.Modbus.Polling;

/// <summary>
/// One connection together with the read plan built for it on connect. <see cref="Dispose"/> may be called
/// concurrently to abort an in-flight read.
/// </summary>
internal sealed class ModbusSession : IDisposable
{
    public ModbusSession(ModbusConnection connection, ModbusPoller poller)
    {
        Connection = connection;
        Poller = poller;
    }

    public ModbusConnection Connection { get; }

    public ModbusPoller Poller { get; }

    /// <inheritdoc cref="ModbusPoller.ReadAsync" />
    public Task ReadAsync(CancellationToken cancellationToken) => Poller.ReadAsync(Connection, cancellationToken);

    public void Dispose() => Connection.Dispose();
}

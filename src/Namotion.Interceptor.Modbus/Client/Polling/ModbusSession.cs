using Namotion.Interceptor.Modbus.Client.Transport;

namespace Namotion.Interceptor.Modbus.Client.Polling;

/// <summary>
/// One connection together with the read plan built for it on connect. <see cref="Dispose"/> may be called
/// concurrently to abort an in-flight read.
/// </summary>
internal sealed class ModbusSession : IDisposable
{
    private readonly ModbusConnection _connection;

    public ModbusSession(ModbusConnection connection, ModbusPoller poller)
    {
        _connection = connection;
        Poller = poller;
    }

    public ModbusPoller Poller { get; }

    /// <inheritdoc cref="ModbusPoller.ReadAsync" />
    public Task ReadAsync(CancellationToken cancellationToken) => Poller.ReadAsync(_connection, cancellationToken);

    public void Dispose() => _connection.Dispose();
}

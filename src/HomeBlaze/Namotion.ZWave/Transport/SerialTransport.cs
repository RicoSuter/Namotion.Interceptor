using System.IO.Ports;

namespace Namotion.ZWave.Transport;

/// <summary>
/// Connects to a controller on a serial port, such as a USB stick, at 115200 baud, 8 data bits, no parity, 1 stop bit,
/// and asserts DTR and RTS once the port is open.
/// </summary>
public sealed class SerialTransport : IZWaveTransport
{
    private const int BaudRate = 115200;

    public SerialTransport(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        PortName = portName;
    }

    /// <summary>
    /// The name of the serial port, such as <c>/dev/serial/by-id/usb-0658_0200-if00</c> or <c>COM3</c>. On Linux, a
    /// <c>/dev/serial/by-id/</c> path is recommended: it stays the same, while a <c>/dev/ttyACM0</c> can be renumbered
    /// when the stick re-enumerates on USB.
    /// </summary>
    public string PortName { get; }

    /// <summary>
    /// Returns the serial port, such as "serial port /dev/ttyACM0".
    /// </summary>
    public override string ToString() => $"serial port {PortName}";

    public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var port = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One);
        try
        {
            port.Open();

            // Some USB serial adapters only pass data while DTR and RTS are asserted.
            port.DtrEnable = true;
            port.RtsEnable = true;
            return ValueTask.FromResult<Stream>(new SerialPortStream(port));
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }
}

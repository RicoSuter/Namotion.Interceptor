using System.IO.Ports;

namespace Namotion.ZWave.Transport;

/// <summary>
/// Connects to a controller on a serial port, such as a USB stick, at 115200 baud, 8 data bits, no parity, 1 stop bit.
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
    /// The name of the serial port, such as <c>/dev/ttyACM0</c> or <c>COM3</c>.
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
            return ValueTask.FromResult<Stream>(new SerialPortStream(port));
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }
}

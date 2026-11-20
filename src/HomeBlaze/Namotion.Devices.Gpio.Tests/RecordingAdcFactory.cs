using System.Device.I2c;
using System.Device.Spi;
using Iot.Device.Adc;
using Iot.Device.Ads1115;
using Namotion.Devices.Gpio.Configuration;

namespace Namotion.Devices.Gpio.Tests;

/// <summary>
/// Creates an MCP3008 and an ADS1115 on devices that touch no hardware, and records each creation and disposal in
/// <see cref="Events"/>, such as "create MCP3008 21" and "dispose MCP3008 21" with the clock pin, or "create ADS1115 1"
/// with the I2C bus.
/// </summary>
internal sealed class RecordingAdcFactory
{
    private readonly Lock _lock = new();
    private readonly List<string> _events = new();

    /// <summary>
    /// Whether the next creation of an MCP3008 throws an <see cref="IOException"/>, once.
    /// </summary>
    public bool FailsNextMcp3008Creation { get; set; }

    /// <summary>
    /// Called with each recorded event, after it was recorded.
    /// </summary>
    public Action<string>? EventRecorded { get; set; }

    /// <summary>
    /// Gets the recorded events, in order.
    /// </summary>
    public string[] Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets how many events start with the given text.
    /// </summary>
    public int Count(string eventPrefix) => Events.Count(recordedEvent => recordedEvent.StartsWith(eventPrefix, StringComparison.Ordinal));

    public Mcp3008 CreateMcp3008(Mcp3008Configuration configuration)
    {
        if (FailsNextMcp3008Creation)
        {
            FailsNextMcp3008Creation = false;
            throw new IOException("No MCP3008 answered.");
        }

        Record($"create MCP3008 {configuration.ClockPin}");
        return new Mcp3008(new RecordingSpiDevice(() => Record($"dispose MCP3008 {configuration.ClockPin}")));
    }

    public Ads1115 CreateAds1115(Ads1115Configuration configuration)
    {
        Record($"create ADS1115 {configuration.I2cBus}");
        return new Ads1115(new RecordingI2cDevice(
            new I2cConnectionSettings(configuration.I2cBus, configuration.Address),
            () => Record($"dispose ADS1115 {configuration.I2cBus}")));
    }

    private void Record(string recordedEvent)
    {
        lock (_lock)
        {
            _events.Add(recordedEvent);
        }

        EventRecorded?.Invoke(recordedEvent);
    }

    private sealed class RecordingSpiDevice(Action disposed) : SpiDevice
    {
        public override SpiConnectionSettings ConnectionSettings { get; } = new(0, 0);

        public override byte ReadByte() => 0;

        public override void Read(Span<byte> buffer) => buffer.Clear();

        public override void WriteByte(byte value)
        {
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
        }

        public override void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer) => readBuffer.Clear();

        protected override void Dispose(bool disposing)
        {
            disposed();
            base.Dispose(disposing);
        }
    }

    // Reads all bits set, which an ADS1115 reports as no conversion running.
    private sealed class RecordingI2cDevice(I2cConnectionSettings connectionSettings, Action disposed) : I2cDevice
    {
        public override I2cConnectionSettings ConnectionSettings { get; } = connectionSettings;

        public override byte ReadByte() => 0xFF;

        public override void Read(Span<byte> buffer) => buffer.Fill(0xFF);

        public override void WriteByte(byte value)
        {
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
        }

        public override void WriteRead(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer) => readBuffer.Fill(0xFF);

        protected override void Dispose(bool disposing)
        {
            disposed();
            base.Dispose(disposing);
        }
    }
}

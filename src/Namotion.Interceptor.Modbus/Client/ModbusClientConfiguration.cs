using Namotion.Interceptor.Modbus.Mapping;

namespace Namotion.Interceptor.Modbus.Client;

/// <summary>
/// Configuration of a Modbus TCP client source.
/// </summary>
public sealed class ModbusClientConfiguration
{
    private const int MaximumRegisterGapLimit = ModbusReadPlanner.MaximumRegistersPerRequest - 1;
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Gets the host name or IP address of the Modbus TCP server.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Gets the TCP port of the Modbus TCP server. Default is 502.
    /// </summary>
    public int Port { get; init; } = 502;

    /// <summary>
    /// Gets the unit ID for registers of subjects with no <see cref="IModbusUnitIdProvider"/> on themselves or an ancestor.
    /// Otherwise the nearest provider's unit ID applies. Default is 1.
    /// </summary>
    public byte UnitId { get; init; } = 1;

    /// <summary>
    /// Gets the time between poll cycles. Default is 2 seconds.
    /// </summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the timeout for connecting and for each request. A timeout is treated as a lost connection. Default is 5 seconds.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the delay before reconnecting after a lost connection or a failed connect attempt, also used as the
    /// retry time of the underlying subject source. Default is 10 seconds.
    /// </summary>
    public TimeSpan RetryTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the time the change queue buffers changes before processing them. Default is 8 milliseconds.
    /// </summary>
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets how many unmapped registers or bits a read request may span to merge neighbouring mappings.
    /// 0 reads strictly contiguous blocks, which is safe for devices that reject reads of unmapped addresses. Default is 0.
    /// </summary>
    public int MaximumRegisterGap { get; init; }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when a value is out of range.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("Host must be specified.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException($"Port must be between 1 and 65535, got: {Port}");
        }

        ValidateDelay(PollingInterval, allowZero: false, nameof(PollingInterval));
        ValidateDelay(RequestTimeout, allowZero: false, nameof(RequestTimeout));
        ValidateDelay(RetryTime, allowZero: false, nameof(RetryTime));
        ValidateDelay(BufferTime, allowZero: true, nameof(BufferTime));

        if (MaximumRegisterGap is < 0 or > MaximumRegisterGapLimit)
        {
            throw new ArgumentException($"MaximumRegisterGap must be between 0 and {MaximumRegisterGapLimit}, got: {MaximumRegisterGap}");
        }
    }

    private static void ValidateDelay(TimeSpan value, bool allowZero, string name)
    {
        if (value < TimeSpan.Zero || (!allowZero && value == TimeSpan.Zero) || value > MaximumDelay)
        {
            var lowerBound = allowZero ? "between 0" : "greater than 0";
            throw new ArgumentException($"{name} must be {lowerBound} and at most {MaximumDelay}, got: {value}");
        }
    }
}

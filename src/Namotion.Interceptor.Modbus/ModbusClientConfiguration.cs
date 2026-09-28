namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Configuration of a Modbus TCP client source.
/// </summary>
public sealed class ModbusClientConfiguration
{
    /// <summary>
    /// Gets the host name or IP address of the Modbus TCP server.
    /// </summary>
    public required string Host { get; init; }

    public int Port { get; init; } = 502;

    /// <summary>
    /// Gets the unit ID used for registers of subjects without <see cref="IModbusUnitIdProvider"/> or
    /// <see cref="Attributes.ModbusUnitIdAttribute"/> in their ancestry.
    /// </summary>
    public byte UnitId { get; init; } = 1;

    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the timeout for connecting and for each request. A timeout is treated as a lost connection.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the delay before reconnecting after a failure.
    /// </summary>
    public TimeSpan RetryTime { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets how many unmapped registers or bits a read request may span to merge neighbouring mappings.
    /// 0 reads strictly contiguous blocks, which is safe for devices that reject reads of unmapped addresses.
    /// </summary>
    public int MaximumRegisterGap { get; init; }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when a value is out of range.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("Host must be specified.", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException($"Port must be between 1 and 65535, got: {Port}", nameof(Port));
        }

        if (PollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException($"PollingInterval must be positive, got: {PollingInterval}", nameof(PollingInterval));
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"RequestTimeout must be positive, got: {RequestTimeout}", nameof(RequestTimeout));
        }

        if (RetryTime <= TimeSpan.Zero)
        {
            throw new ArgumentException($"RetryTime must be positive, got: {RetryTime}", nameof(RetryTime));
        }

        if (BufferTime < TimeSpan.Zero)
        {
            throw new ArgumentException($"BufferTime must not be negative, got: {BufferTime}", nameof(BufferTime));
        }

        if (MaximumRegisterGap is < 0 or > 124)
        {
            throw new ArgumentException($"MaximumRegisterGap must be between 0 and 124, got: {MaximumRegisterGap}", nameof(MaximumRegisterGap));
        }
    }
}

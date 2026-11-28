namespace Namotion.Matter.Server;

/// <summary>Options of a <see cref="MatterServerController"/>. Every value must be positive.</summary>
public sealed record MatterServerOptions
{
    // CancellationTokenSource.CancelAfter accepts at most this.
    private static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    /// <summary>
    /// Gets how long a request other than commissioning waits for its answer, how long sending any request may take,
    /// and how long connecting waits for the server's information. The default is 60 seconds, above the server's own
    /// response timeout for a device.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the largest message accepted from the server, in bytes. The default is 64 MB.</summary>
    public int MaxMessageSize { get; init; } = 64 * 1024 * 1024;

    /// <summary>Gets how often the connection sends a WebSocket ping to the server. The default is 30 seconds.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long the connection waits for the server's answer to a ping before it treats the connection as lost.
    /// The default is 30 seconds.
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <exception cref="ArgumentOutOfRangeException">A value is not positive, or too large.</exception>
    internal void ThrowIfInvalid()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RequestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RequestTimeout, MaxRequestTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxMessageSize, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(MaxMessageSize, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(KeepAliveInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(KeepAliveTimeout, TimeSpan.Zero);
    }
}

namespace Namotion.ZWave;

/// <summary>
/// Options of a connection to a Z-Wave controller. The defaults follow the Host API specification.
/// </summary>
public sealed record ZWaveControllerOptions
{
    /// <summary>
    /// How long to wait for the controller to acknowledge a frame. Default: 1600 ms.
    /// </summary>
    public TimeSpan AcknowledgementTimeout { get; init; } = TimeSpan.FromMilliseconds(1600);

    /// <summary>
    /// How long to wait for the response to a request after it was acknowledged. Default: 10 s.
    /// </summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often a frame is written before the request fails, 1 to 10. Default: 3.
    /// </summary>
    public int MaximumSendAttempts { get; init; } = 3;

    /// <summary>
    /// The time provider of every timeout and delay.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(AcknowledgementTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ResponseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumSendAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumSendAttempts, 10);
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }
}

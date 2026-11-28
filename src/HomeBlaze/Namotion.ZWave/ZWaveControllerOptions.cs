using System.Runtime.CompilerServices;

namespace Namotion.ZWave;

/// <summary>
/// Options of a connection to a Z-Wave controller. The defaults follow the Host API specification. Every timeout must be
/// positive and at most <see cref="uint.MaxValue"/> - 1 ms, the longest timer a task accepts.
/// </summary>
public sealed record ZWaveControllerOptions
{
    // The longest timeout Task.Delay and Task.WaitAsync accept.
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// How long to wait for the controller to acknowledge a frame, and for a write to complete; a write that takes longer
    /// fails the connection. Default: 1600 ms.
    /// </summary>
    public TimeSpan AcknowledgementTimeout { get; init; } = TimeSpan.FromMilliseconds(1600);

    /// <summary>
    /// How long to wait for the response to a request after it was acknowledged. Default: 10 s.
    /// </summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a frame may take from its start of frame byte to its last byte; an incomplete frame is dropped after
    /// it, and the bytes after its start of frame byte are parsed again. Default: 1500 ms.
    /// </summary>
    public TimeSpan FrameDeliveryTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

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
        ValidateTimeout(AcknowledgementTimeout);
        ValidateTimeout(ResponseTimeout);
        ValidateTimeout(FrameDeliveryTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumSendAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumSendAttempts, 10);
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }

    private static void ValidateTimeout(TimeSpan timeout, [CallerArgumentExpression(nameof(timeout))] string? parameterName = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, MaximumTimeout, parameterName);
    }
}

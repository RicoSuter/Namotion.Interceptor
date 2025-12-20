using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a single GPIO pin with mode, value, and availability status.
/// </summary>
[InterceptorSubject]
public partial class GpioPin : IHostedSubject, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The GPIO pin number (BCM numbering).
    /// </summary>
    public partial int PinNumber { get; set; }

    /// <summary>
    /// The pin operating mode.
    /// </summary>
    [Configuration]
    public partial GpioPinMode Mode { get; set; }

    /// <summary>
    /// The current pin value (true = high, false = low).
    /// </summary>
    [State]
    public partial bool Value { get; set; }

    /// <summary>
    /// Pin availability status.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Status message (e.g., "Reserved for I2C", "Write verification failed").
    /// </summary>
    [State]
    public partial string? StatusMessage { get; set; }

    /// <inheritdoc />
    public string? Title => $"Pin {PinNumber}";

    /// <inheritdoc />
    public string? Icon => "Settings";

    public GpioPin()
    {
        PinNumber = 0;
        Mode = GpioPinMode.Input;
        Value = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }
}

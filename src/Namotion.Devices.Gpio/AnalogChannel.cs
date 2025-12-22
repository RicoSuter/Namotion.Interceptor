using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents an analog input channel from an ADC.
/// </summary>
[InterceptorSubject]
public partial class AnalogChannel : IHostedSubject, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The ADC channel number.
    /// </summary>
    public partial int ChannelNumber { get; set; }

    /// <summary>
    /// The normalized value (0.0 to 1.0).
    /// </summary>
    [State]
    public partial double Value { get; set; }

    /// <summary>
    /// The raw ADC value (e.g., 0-1023 for 10-bit).
    /// </summary>
    [State]
    public partial int RawValue { get; set; }

    /// <summary>
    /// Channel availability status.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Status message for errors or info.
    /// </summary>
    [State]
    public partial string? StatusMessage { get; set; }

    /// <inheritdoc />
    public string Title => $"Channel {ChannelNumber}";

    /// <inheritdoc />
    public string Icon => "ShowChart";

    public AnalogChannel()
    {
        ChannelNumber = 0;
        Value = 0.0;
        RawValue = 0;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }
}

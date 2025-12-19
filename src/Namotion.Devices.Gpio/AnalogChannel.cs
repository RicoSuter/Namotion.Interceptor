using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents an analog input channel from an ADC.
/// </summary>
[InterceptorSubject]
public partial class AnalogChannel
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

    public AnalogChannel()
    {
        ChannelNumber = 0;
        Value = 0.0;
        RawValue = 0;
    }
}

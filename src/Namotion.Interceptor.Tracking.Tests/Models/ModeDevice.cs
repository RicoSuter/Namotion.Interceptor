using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

public enum DeviceMode
{
    Idle = 0,
    Running = 1,
    Fault = 2
}

/// <summary>
/// Test model with an enum property. OPC UA delivers enumeration values as boxed integers (the
/// enum's underlying type), which the generated setter unboxes leniently; the origin survival and
/// correction divergence checks must mirror that leniency.
/// </summary>
[InterceptorSubject]
public partial class ModeDevice
{
    public partial DeviceMode Mode { get; set; }

    public partial DeviceMode? OptionalMode { get; set; }
}

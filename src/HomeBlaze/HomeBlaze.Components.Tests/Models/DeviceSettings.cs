using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Tests.Models;

/// <summary>
/// A subject that is not configurable itself, so its configuration owner is the device that holds it, if any.
/// </summary>
[InterceptorSubject]
public partial class DeviceSettings
{
    [Configuration]
    public partial int Level { get; set; }
}

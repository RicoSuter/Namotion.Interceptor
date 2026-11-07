using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>A device with a state value.</summary>
[InterceptorSubject]
public partial class PermissionsTestDevice
{
    [State]
    public partial double Temperature { get; set; }
}

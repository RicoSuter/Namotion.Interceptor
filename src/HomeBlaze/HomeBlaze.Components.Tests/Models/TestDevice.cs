using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Tests.Models;

[InterceptorSubject]
public partial class TestDevice
{
    public partial string? Name { get; set; }
}

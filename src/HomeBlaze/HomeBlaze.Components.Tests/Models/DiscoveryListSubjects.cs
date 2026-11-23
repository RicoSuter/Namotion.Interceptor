using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Tests.Models;

[InterceptorSubject]
public partial class DiscoveryListHubSubject
{
    public partial string? HostAddress { get; set; }
}

[InterceptorSubject]
public partial class DiscoveryListSwitchSubject
{
    public partial string? HostAddress { get; set; }
}

using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models;

// Two static attribute properties for one (property, attribute) pair. The generator reports this
// shape as NI0066; it is suppressed here so the registry's own resolution at registration is testable.
#pragma warning disable NI0066
[InterceptorSubject]
public partial class DuplicateAttributeTarget
{
    public partial bool Forward { get; set; }

    [PropertyAttribute(nameof(Forward), "IsEnabled")]
    public partial bool Forward_IsEnabled { get; set; }

    [PropertyAttribute(nameof(Forward), "IsEnabled")]
    public partial bool Forward_Enabled { get; set; }

    public partial DuplicateAttributeTarget? Child { get; set; }
}
#pragma warning restore NI0066

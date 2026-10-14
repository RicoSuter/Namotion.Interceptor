using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models;

[InterceptorSubject]
public partial class SetterAccessSubject
{
    public partial string? PublicSetter { get; set; }

    public partial string? InternalSetter { get; internal set; }

    [Derived]
    public string? NoSetter => PublicSetter;
}

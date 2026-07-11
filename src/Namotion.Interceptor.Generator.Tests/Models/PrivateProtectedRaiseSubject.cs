using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class PrivateProtectedRaiseSubject : PrivateProtectedRaiseBase
{
    public partial string? Name { get; set; }
}

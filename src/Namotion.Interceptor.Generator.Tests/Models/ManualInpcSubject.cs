using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class ManualInpcSubject : ManualInpcBase
{
    public partial string? Name { get; set; }
}

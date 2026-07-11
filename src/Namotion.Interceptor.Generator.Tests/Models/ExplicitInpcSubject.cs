using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class ExplicitInpcSubject : ExplicitInpcBase
{
    public partial string? Name { get; set; }
}

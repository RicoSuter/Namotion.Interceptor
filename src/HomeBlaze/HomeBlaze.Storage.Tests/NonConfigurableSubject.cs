using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

[InterceptorSubject]
public partial class NonConfigurableSubject
{
    public partial string? Name { get; set; }
}

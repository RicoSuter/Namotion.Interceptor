using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Virtual.Tests.Testing;

[InterceptorSubject]
public partial class TestRoot
{
    public TestRoot()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }
}

[InterceptorSubject]
public partial class TestMeter
{
    [State]
    public partial decimal? Power { get; set; }

    [State]
    public partial int Mode { get; set; }

    [State]
    public partial decimal? Energy { get; internal set; }

    [Configuration(IsSecret = true)]
    public partial string? ApiKey { get; set; }
}

using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

[InterceptorSubject]
public partial class TestContainer
{
    public partial string? Name { get; set; }

    public partial TestContainer? Child { get; set; }

    public partial Dictionary<string, TestContainer> Children { get; set; }

    public TestContainer()
    {
        Children = new Dictionary<string, TestContainer>();
    }
}

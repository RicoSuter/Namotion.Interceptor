using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

/// <summary>A subject whose dictionary keys are path segments of their own.</summary>
[InterceptorSubject]
public partial class TestInlineContainer
{
    public TestInlineContainer()
    {
        Children = [];
    }

    public partial string? Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, TestInlineContainer> Children { get; set; }
}

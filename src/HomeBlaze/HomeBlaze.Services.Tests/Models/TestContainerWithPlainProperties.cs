using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>Test model whose subjects are held by properties that are not intercepted.</summary>
[InterceptorSubject]
public partial class TestContainerWithPlainProperties
{
    public TestContainer? Plain { get; set; }

    [Derived]
    public TestContainer? Current => Plain;

    [InlinePaths]
    public Dictionary<string, TestContainer> Children { get; set; } = new();
}

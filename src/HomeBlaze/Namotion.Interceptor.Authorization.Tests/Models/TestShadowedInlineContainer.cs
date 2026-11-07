using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

/// <summary>
/// A subject whose [InlinePaths] property's own segment is taken by another property for the "test" provider, so
/// its keys only resolve bare.
/// </summary>
[InterceptorSubject]
public partial class TestShadowedInlineContainer
{
    public TestShadowedInlineContainer()
    {
        Children = [];
    }

    [Path("test", "Children")]
    public partial string? Label { get; set; }

    [Path("test", "name")]
    public partial string? Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, TestShadowedInlineContainer> Children { get; set; }
}

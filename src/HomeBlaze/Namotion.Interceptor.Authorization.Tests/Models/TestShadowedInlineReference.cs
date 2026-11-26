using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

/// <summary>
/// A subject whose [InlinePaths] property holds a single subject and whose own segment is taken by another property
/// for the "test" provider, so the subject it holds has no path.
/// </summary>
[InterceptorSubject]
public partial class TestShadowedInlineReference
{
    [Path("test", "Child")]
    public partial string? Label { get; set; }

    [InlinePaths]
    public partial Light? Child { get; set; }
}

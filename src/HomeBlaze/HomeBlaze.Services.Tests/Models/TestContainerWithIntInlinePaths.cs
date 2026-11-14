using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>
/// Test model for [InlinePaths] on a dictionary with integer keys.
/// </summary>
[InterceptorSubject]
public partial class TestContainerWithIntInlinePaths
{
    [InlinePaths]
    public partial Dictionary<int, TestContainer> Items { get; set; }

    public TestContainerWithIntInlinePaths()
    {
        Items = new Dictionary<int, TestContainer>();
    }
}

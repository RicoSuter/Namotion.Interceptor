using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>
/// Test model with a collection of child subjects.
/// </summary>
[InterceptorSubject]
public partial class TestContainerWithItems
{
    public partial List<TestContainer> Items { get; set; }

    public TestContainerWithItems()
    {
        Items = [];
    }
}

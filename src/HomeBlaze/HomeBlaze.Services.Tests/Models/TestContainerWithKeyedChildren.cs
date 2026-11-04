using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>
/// Test model with subject dictionaries whose keys are not strings.
/// </summary>
[InterceptorSubject]
public partial class TestContainerWithKeyedChildren
{
    public partial Dictionary<int, TestContainer> Items { get; set; }

    public partial Dictionary<Guid, TestContainer> ItemsById { get; set; }

    public TestContainerWithKeyedChildren()
    {
        Items = new Dictionary<int, TestContainer>();
        ItemsById = new Dictionary<Guid, TestContainer>();
    }
}

using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>
/// Test model with a subject list, whose entries are addressed by position.
/// </summary>
[InterceptorSubject]
public partial class TestContainerWithItemList
{
    public partial List<TestContainer> Items { get; set; }

    public TestContainerWithItemList()
    {
        Items = [];
    }
}

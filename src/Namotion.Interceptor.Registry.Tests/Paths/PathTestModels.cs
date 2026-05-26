using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Paths;

[InterceptorSubject]
public partial class TestContainer
{
    public partial string Name { get; set; }
    public partial Dictionary<string, TestItem> Items { get; set; }

    public TestContainer()
    {
        Items = new Dictionary<string, TestItem>();
    }
}

[InterceptorSubject]
public partial class TestItem
{
    public partial string Value { get; set; }
    public partial Dictionary<string, TestItem> Children { get; set; }

    public TestItem()
    {
        Children = new Dictionary<string, TestItem>();
    }
}

/// <summary>
/// Test model with [InlinePaths] — dictionary keys become direct path segments.
/// </summary>
[InterceptorSubject]
public partial class TestInlineContainer
{
    public partial string Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, TestInlineContainer> Children { get; set; }

    public TestInlineContainer()
    {
        Children = new Dictionary<string, TestInlineContainer>();
    }
}

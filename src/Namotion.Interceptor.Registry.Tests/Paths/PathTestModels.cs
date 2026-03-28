using Namotion.Interceptor.Attributes;

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

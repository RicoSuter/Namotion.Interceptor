using System.Collections.Generic;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

[InterceptorSubject]
public partial class TestRoot
{
    public TestRoot()
    {
        Name = "";
        Items = [];
        Lookup = new Dictionary<string, TestItem>();
    }

    public partial bool Connected { get; set; }
    public partial string Name { get; set; }
    public partial decimal Number { get; set; }
    public partial TestItem[] Items { get; set; }
    public partial TestItem? Child { get; set; }
    public partial Dictionary<string, TestItem> Lookup { get; set; }
}

[InterceptorSubject]
public partial class TestItem
{
    public TestItem()
    {
        Label = "";
    }

    public partial string Label { get; set; }
    public partial int Value { get; set; }
}

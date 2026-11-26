using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

/// <summary>A subject with a dictionary of its own type, to build dense parent graphs.</summary>
[InterceptorSubject]
public partial class TestItem
{
    public TestItem()
    {
        Children = [];
    }

    public partial string? Value { get; set; }

    public partial Dictionary<string, TestItem> Children { get; set; }
}

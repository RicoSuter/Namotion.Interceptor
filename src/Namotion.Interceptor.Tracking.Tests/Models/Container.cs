using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Test model with a Dictionary property for testing lifecycle reference counting.
/// </summary>
[InterceptorSubject]
public partial class Container
{
    public Container()
    {
        Children = new Dictionary<string, Person>();
    }

    public partial string? Name { get; set; }

    public partial Dictionary<string, Person> Children { get; set; }

    public override string ToString() => $"{{Container: {Name}}}";
}

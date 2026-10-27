using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A node that a concurrency test moves between parents. Its derived properties read its own
/// properties, a derived property with a setter, a dependency set that changes, and its child.
/// Most are declared before the properties they read, so that attaching the subject again attaches
/// them before their sources, as a hash layout of the property keys can.
/// </summary>
[InterceptorSubject]
public partial class StressNode
{
    [Derived]
    public int Shifted => Value + Offset;

    [Derived]
    public int Selected => UseAlternate ? Alternate : Value;

    [Derived]
    public int ChildSelected => Child?.Selected ?? -1;

    public partial int Value { get; set; }

    public partial int Alternate { get; set; }

    public partial bool UseAlternate { get; set; }

    public partial StressNode? Child { get; set; }

    [Derived]
    public partial int Offset { get; set; }

    [Derived]
    public int Doubled => Value * 2;
}

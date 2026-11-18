using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class FieldOwnerRoot
{
    public partial FieldOwnerPair? Pair { get; set; }
}

/// <summary>
/// The tests rely on <see cref="First"/> attaching before <see cref="Second"/>.
/// </summary>
[InterceptorSubject]
public partial class FieldOwnerPair
{
    public partial FieldOwnedNode? First { get; set; }

    public partial FieldOwnedNode? Second { get; set; }
}

/// <summary>
/// Reaches its sibling through a plain field rather than through parent tracking.
/// </summary>
[InterceptorSubject]
public partial class FieldOwnedNode
{
    public FieldOwnerPair? Owner;

    public partial int Value { get; set; }

    [Derived]
    public int SiblingValue => (ReferenceEquals(Owner?.First, this) ? Owner?.Second : Owner?.First)?.Value ?? -1;
}

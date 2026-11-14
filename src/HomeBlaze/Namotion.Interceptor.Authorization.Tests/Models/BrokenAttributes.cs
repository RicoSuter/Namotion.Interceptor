using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

/// <summary>A subject whose attribute names a property that does not exist.</summary>
[InterceptorSubject]
public partial class OrphanAttributeHolder
{
    public partial string? Value { get; set; }

    [PropertyAttribute("Missing", "Unit")]
    public partial string? Missing_Unit { get; set; }
}

/// <summary>A subject whose two attributes name each other.</summary>
[InterceptorSubject]
public partial class CyclicAttributeHolder
{
    public partial string? Value { get; set; }

    [PropertyAttribute("B", "a")]
    public partial string? A { get; set; }

    [PropertyAttribute("A", "b")]
    public partial string? B { get; set; }
}

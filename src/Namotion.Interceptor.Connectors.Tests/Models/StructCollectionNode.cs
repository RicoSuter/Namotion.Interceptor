using System.Collections.Immutable;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Model whose collection properties are left at their default value, which for a struct
/// collection is an instance holding a null inner array. Unlike
/// <see cref="ReadOnlyTypesTestNode"/>, nothing initializes them in the constructor, so the
/// connector update path sees the unusable default that a fresh subject actually has.
/// </summary>
[InterceptorSubject]
public partial class StructCollectionNode
{
    public partial string? Name { get; set; }

    /// <summary>Declared as a struct collection, so it starts as <c>default(ImmutableArray&lt;&gt;)</c>.</summary>
    public partial ImmutableArray<StructCollectionNode> ImmutableItems { get; set; }

    /// <summary>Interface-declared, so it can hold a boxed default struct and still be written back as a list.</summary>
    public partial IReadOnlyList<StructCollectionNode>? InterfaceItems { get; set; }
}

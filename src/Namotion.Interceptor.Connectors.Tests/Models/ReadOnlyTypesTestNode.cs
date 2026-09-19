using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Model exercising read-only property type declarations (<see cref="IReadOnlyList{T}"/>,
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/>, <see cref="ImmutableArray{T}"/>). Runtime values
/// are still concrete BCL types that implement the non-generic dispatch interfaces; the test
/// purpose is to confirm the connector update path classifies and round-trips these read-only
/// property types correctly.
/// </summary>
[InterceptorSubject]
public partial class ReadOnlyTypesTestNode
{
    public ReadOnlyTypesTestNode()
    {
        ReadOnlyItems = [];
        ImmutableItems = [];
        WrappedItems = ReadOnlyCollection<ReadOnlyTypesTestNode>.Empty;
        ReadOnlyLookup = new Dictionary<string, ReadOnlyTypesTestNode>();
    }

    public partial string? Name { get; set; }

    public partial IReadOnlyList<ReadOnlyTypesTestNode> ReadOnlyItems { get; set; }

    public partial ImmutableArray<ReadOnlyTypesTestNode> ImmutableItems { get; set; }

    /// <summary>
    /// A read-only wrapper that is built from the working list through its constructor, unlike
    /// <see cref="ImmutableItems"/>, which is built from a static empty instance plus AddRange.
    /// </summary>
    public partial ReadOnlyCollection<ReadOnlyTypesTestNode> WrappedItems { get; set; }

    public partial IReadOnlyDictionary<string, ReadOnlyTypesTestNode> ReadOnlyLookup { get; set; }
}

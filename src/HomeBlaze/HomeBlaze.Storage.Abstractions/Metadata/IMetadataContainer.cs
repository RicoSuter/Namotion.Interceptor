using System.Collections.Immutable;

namespace HomeBlaze.Storage.Abstractions.Metadata;

/// <summary>
/// A container that shows its children in an order of its own, which the key <see cref="MetadataKeys.Order"/> of its
/// metadata holds.
/// </summary>
public interface IMetadataContainer
{
    /// <summary>
    /// The name of the property that holds the children of a container, keyed by their names.
    /// </summary>
    public const string ChildrenPropertyName = "Children";

    /// <summary>
    /// Gets the names of the children in the order they are shown. Children that are not listed follow the listed ones.
    /// Empty when the container has no order.
    /// </summary>
    ImmutableArray<string> ChildOrder { get; }
}

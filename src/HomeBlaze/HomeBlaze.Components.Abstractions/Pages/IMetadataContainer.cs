using System.Collections.Immutable;

namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// A container that shows its children in an order of its own and has metadata of its own. Both are changed through
/// its metadata subject, see <see cref="IMetadataSubject"/>.
/// </summary>
public interface IMetadataContainer
{
    /// <summary>
    /// The name of the property that holds the children of a container, keyed by their names.
    /// </summary>
    public const string ChildrenPropertyName = "Children";

    /// <summary>
    /// Gets the metadata of the container itself, null when it has none.
    /// </summary>
    FolderMetadata? Metadata { get; }

    /// <summary>
    /// Gets the names of the children in the order they are shown. Children that are not listed follow the listed ones.
    /// Empty when the container has no order.
    /// </summary>
    ImmutableArray<string> ChildOrder { get; }
}

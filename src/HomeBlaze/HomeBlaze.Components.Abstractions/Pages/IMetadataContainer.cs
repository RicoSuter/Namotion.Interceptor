using System.Collections.Immutable;
using Namotion.Interceptor;

namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// A container that stores the order of its children, its own metadata and the metadata of the subjects in it.
/// </summary>
public interface IMetadataContainer
{
    /// <summary>
    /// The name of the property that holds the children of a container, keyed by their names.
    /// </summary>
    public const string ChildrenPropertyName = "Children";

    /// <summary>
    /// Gets whether the container has metadata of its own. False for the root of a storage, which only has an order.
    /// </summary>
    bool HasOwnMetadata { get; }

    /// <summary>
    /// Gets the metadata of the container itself, null when it has none.
    /// </summary>
    FolderMetadata? Metadata { get; }

    /// <summary>
    /// Gets the names of the children in the order they are shown. Children that are not listed follow the listed ones.
    /// Empty when the container has no order.
    /// </summary>
    ImmutableArray<string> ChildOrder { get; }

    /// <summary>
    /// Writes the metadata of the container itself. Null or a value with no field set removes it.
    /// </summary>
    /// <exception cref="NotSupportedException">The container has no metadata of its own, see <see cref="HasOwnMetadata"/>.</exception>
    Task WriteMetadataAsync(FolderMetadata? metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the metadata of a subject stored as a JSON file in the container. Null or a value with no field set
    /// removes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject is not the subject of a JSON file in this storage.</exception>
    Task WriteChildMetadataAsync(IInterceptorSubject child, SubjectMetadata? metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a child to the place of another child in the order in which they are shown, shifting the children in
    /// between, and stores the whole resulting order.
    /// </summary>
    /// <remarks>Moving a child to its own place stores the current order unchanged.</remarks>
    /// <param name="name">The name of the child to move.</param>
    /// <param name="targetName">The name of the child whose place it takes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">One of the names is not a child of the container. The message names it.</exception>
    Task MoveChildAsync(string name, string targetName, CancellationToken cancellationToken);
}

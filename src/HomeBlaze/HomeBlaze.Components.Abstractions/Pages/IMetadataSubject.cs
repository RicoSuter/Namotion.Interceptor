using System.Text.Json;

namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// The metadata of a subject stored as a JSON file, of a folder or of the root of a storage, in the dynamic registry
/// property <see cref="PropertyName"/> of that item. The browser changes the metadata and the order through it, as
/// agents do through its methods.
/// </summary>
public interface IMetadataSubject
{
    /// <summary>
    /// The name of the dynamic registry property that holds the metadata subject of an item.
    /// </summary>
    public const string PropertyName = "HomeBlaze:Metadata";

    /// <summary>
    /// Gets the navigation metadata of a subject stored as a JSON file, from the file beside it; null without one, and
    /// for a folder or the root.
    /// </summary>
    SubjectMetadata? SubjectMetadata { get; }

    /// <summary>
    /// Merges values, a JSON object, into the metadata file of the item at the top level: a key with a value is set,
    /// a key with null is removed, and every other key stays.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The values are refused (no JSON object, a key given twice without regard to case, <c>order</c> of a folder, a
    /// known key with a value of another kind, or a limit), the metadata file could not be read, the storage is not
    /// connected, or the item is no longer in a storage. The message says which. A failure to write the file keeps its
    /// own type.
    /// </exception>
    Task SetMetadataAsync(JsonElement values, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the order of the children of a folder or the root: the given names first, then the other children in the
    /// order in which they are shown now.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The item is no folder or root, a name is given twice or is no child, the metadata file could not be read, the
    /// storage is not connected, or the folder is no longer in it. The message says which.
    /// </exception>
    Task SetOrderAsync(string[] names, CancellationToken cancellationToken);
}

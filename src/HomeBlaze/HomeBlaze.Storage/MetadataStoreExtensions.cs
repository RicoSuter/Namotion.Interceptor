using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;

namespace HomeBlaze.Storage;

/// <summary>
/// Writes the order of a folder through its storage.
/// </summary>
public static class MetadataStoreExtensions
{
    /// <summary>
    /// Sets the order of the children of a folder or of the root of a storage, in the key
    /// <see cref="MetadataKeys.Order"/> of its metadata: the given names first, in the given order, then the other
    /// children in the order in which they are shown when the order is written. Of two writes at the same time, the
    /// later one wins.
    /// </summary>
    /// <param name="folder">An <see cref="IMetadataContainer"/>: a folder, or a storage, whose order is its own.</param>
    /// <param name="names">The names of the children to show first, as <see cref="ChildOrdering.GetShownNames"/> gives them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">
    /// The subject is no folder or storage or is in none, a name is null, given more than once or no child, or the
    /// store refused the write; the message says why.
    /// </exception>
    public static Task SetOrderAsync(this IInterceptorSubject folder, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        var storage = folder.FindMetadataStore() as FluentStorageContainer
            ?? throw new StorageRefusedException("The folder is in no storage.");
        return storage.WriteOrderAsync(folder, [.. names], cancellationToken);
    }
}

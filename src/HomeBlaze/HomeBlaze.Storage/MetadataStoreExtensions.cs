using System.Buffers;
using System.Text.Json;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;

namespace HomeBlaze.Storage;

/// <summary>
/// Writes the order of a folder through its <see cref="IMetadataStore"/>.
/// </summary>
public static class MetadataStoreExtensions
{
    /// <summary>
    /// Sets the order of the children of a folder or of the root of a storage, in the key
    /// <see cref="MetadataKeys.Order"/> of its metadata: the given names first, in the given order, then the other
    /// children in the order in which they are shown now. Of two writes at the same time, the later one wins.
    /// </summary>
    /// <param name="folder">An <see cref="IMetadataContainer"/>: a folder, or a storage, whose order is its own.</param>
    /// <param name="names">The names of the children to show first, as <see cref="ChildOrdering.GetShownNames"/> gives them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">
    /// The subject is no folder or storage, or is in none, or a name is null, given more than once or no child. The
    /// message names them.
    /// </exception>
    /// <exception cref="InvalidOperationException">The store refused the write; the message says why.</exception>
    public static Task SetOrderAsync(this IInterceptorSubject folder, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (folder is not IMetadataContainer)
        {
            throw new ArgumentException("Only a folder or the root of a storage has an order.", nameof(folder));
        }

        var store = folder.FindMetadataStore()
            ?? throw new ArgumentException("The folder is in no storage.", nameof(folder));

        // Null arrives from an agent that sends a JSON null in the array.
        if (names.Any(static name => name is null))
        {
            throw new ArgumentException("A name is null.", nameof(names));
        }

        var shownNames = ChildOrdering.GetShownNames(folder);
        if (ChildOrderNames.GetError(names, shownNames) is { } error)
        {
            throw new ArgumentException(error, nameof(names));
        }

        var listedNames = new HashSet<string>(names, StringComparer.Ordinal);
        return store.SetMetadataAsync(folder, null, CreateOrder([.. names, .. shownNames.Where(name => !listedNames.Contains(name))]), cancellationToken);
    }

    private static JsonElement CreateOrder(IEnumerable<string> names)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray(MetadataKeys.Order);
            foreach (var name in names)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonElement.Parse(buffer.WrittenSpan);
    }
}

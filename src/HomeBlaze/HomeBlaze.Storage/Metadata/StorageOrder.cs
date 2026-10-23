using System.Buffers;
using System.Text.Json;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions.Metadata;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Metadata;

/// <summary>
/// The order of the children of a folder or of the root of a storage, in the key <see cref="MetadataKeys.Order"/> of
/// its metadata.
/// </summary>
internal static class StorageOrder
{
    /// <summary>
    /// Creates metadata whose order holds the given names first, then the other children in the order in which they are shown.
    /// </summary>
    public static JsonElement Create(IInterceptorSubject folder, string[] names)
    {
        var listedNames = new HashSet<string>(names, StringComparer.Ordinal);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray(MetadataKeys.Order);
            foreach (var name in names)
            {
                writer.WriteStringValue(name);
            }

            foreach (var name in ChildOrdering.GetShownNames(folder))
            {
                if (!listedNames.Contains(name))
                {
                    writer.WriteStringValue(name);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonElement.Parse(buffer.WrittenSpan);
    }

    /// <summary>
    /// Gets why the order in the values is refused, or null when the values have none or it is valid: the names of an
    /// order are children of the folder, each given once, which also bounds the size of the order.
    /// </summary>
    /// <remarks>Reads the names as strings, so the values must have been accepted by <see cref="StorageMetadataWriter"/>.</remarks>
    public static string? GetError(IInterceptorSubject folder, JsonElement values)
    {
        if (!values.TryGetProperty(MetadataKeys.Order, out var order) || order.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var children = new HashSet<string>(ChildOrdering.GetShownNames(folder), StringComparer.Ordinal);
        var listedNames = new HashSet<string>(StringComparer.Ordinal);
        var repeatedNames = new SortedSet<string>(StringComparer.Ordinal);
        var unknownNames = new List<string>();
        foreach (var item in order.EnumerateArray())
        {
            var name = item.GetString()!;
            if (!listedNames.Add(name))
            {
                repeatedNames.Add(name);
            }
            else if (!children.Contains(name))
            {
                unknownNames.Add(name);
            }
        }

        if (repeatedNames.Count > 0)
        {
            return $"Names given more than once: {NameList.Format(repeatedNames)}.";
        }

        return unknownNames.Count > 0
            ? $"Names that are not children of the folder: {NameList.Format(unknownNames)}. Its children: {NameList.Format(children.Order(StringComparer.Ordinal))}."
            : null;
    }
}

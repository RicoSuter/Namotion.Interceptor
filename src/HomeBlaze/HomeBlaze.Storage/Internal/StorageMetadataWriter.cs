using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Components.Abstractions.Pages;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Merges values into the entry of a target in the content of a metadata file, and gives the content to write. Keys
/// that HomeBlaze does not know are kept as they are.
/// </summary>
internal static class StorageMetadataWriter
{
    /// <summary>The most keys of an entry, and of the values of a write.</summary>
    public const int MaxKeyCount = 64;

    /// <summary>The longest name of a key.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The longest string that is the value of a key.</summary>
    public const int MaxStringLength = 256;

    /// <summary>The largest value of a key, serialized, except <c>order</c>.</summary>
    public const int MaxValueSize = 4 * 1024;

    /// <summary>The largest file, which a write that does not make the file larger may exceed.</summary>
    public const int MaxFileSize = 512 * 1024;

    private static readonly string[] KnownKeys =
    [
        MetadataKeys.Title, MetadataKeys.NavigationTitle, MetadataKeys.Icon, MetadataKeys.Location, MetadataKeys.Alignment,
        MetadataKeys.Order
    ];

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonWriterOptions FileOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonWriterOptions CompactOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Merges values into the entry of the target: a key with a value is set, a nested value replacing the old one as
    /// a whole, a key with null is removed, and every other key stays. A key matches the key of the entry that differs
    /// only in case, whose name stays. An entry of a subject or property is found in either notation of its path and
    /// written with <see cref="StorageMetadataTarget.Path"/>. An entry without keys is removed, and so is a section
    /// without entries.
    /// </summary>
    /// <param name="content">The content of the file, which <see cref="StorageMetadataFile.TryParse"/> and
    /// <see cref="CanBeWritten"/> accept; null without a file.</param>
    /// <param name="target">The file and the entry.</param>
    /// <param name="values">A JSON object.</param>
    /// <returns>The content to write, indented and ending with a new line, or null when the file holds no key any more
    /// and is deleted.</returns>
    /// <exception cref="StorageRefusedException">
    /// values is no JSON object, gives a key twice without regard to case, has a key that starts with $, sets
    /// <c>order</c> of anything but a folder or the root, has a known key with a value of another kind, has a key that
    /// cannot be read or a value that cannot be written, or exceeds a limit; or the file has two entries of the target.
    /// The message names the key.
    /// </exception>
    public static byte[]? Merge(byte[]? content, StorageMetadataTarget target, JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Object)
        {
            throw Refuse("values must be a JSON object, for example { \"navTitle\": \"Pump\" }.");
        }

        var root = content is null ? new JsonObject(NodeOptions) : Parse(content);
        var sizeBefore = content is null ? 0 : Serialize(root).Length;
        var (section, entry) = GetEntry(root, target);
        MergeValues(entry, values, isContainer: target is { Section: null, IsContainer: true });

        if (section is not null && CountKeys(entry) == 0)
        {
            section.Remove(target.Path!);
            if (section.Count == 0)
            {
                root.Remove(target.Section!);
            }
        }

        if (!root.Any(pair => pair.Value is not null))
        {
            return null;
        }

        var written = Serialize(root);
        if (written.Length > MaxFileSize && written.Length > sizeBefore)
        {
            throw Refuse($"The metadata file would be larger than {MaxFileSize / 1024} KB with {NameList.Format(values.EnumerateObject().Select(static property => property.Name))}.");
        }

        return written;
    }

    /// <summary>
    /// Gets whether the content can be written back. A value that cannot, such as a string with a lone surrogate, is
    /// found only by writing it, so a pass that reads the file does not look for it.
    /// </summary>
    public static bool CanBeWritten(byte[] content)
    {
        try
        {
            _ = Serialize(Parse(content));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static JsonObject Parse(byte[] content)
    {
        ReadOnlySpan<byte> json = content;
        if (json.StartsWith(Encoding.UTF8.Preamble))
        {
            json = json[Encoding.UTF8.Preamble.Length..];
        }

        return JsonNode.Parse(json, NodeOptions, StorageMetadataFile.DocumentOptions) as JsonObject
            ?? throw new JsonException("A metadata file holds a JSON object.");
    }

    // The section and the entry of the target in it, or no section and the file itself for its own keys. A section is
    // rebuilt with paths that match with regard to case, as paths are names of properties.
    private static (JsonObject? Section, JsonObject Entry) GetEntry(JsonObject root, StorageMetadataTarget target)
    {
        if (target.Section is null)
        {
            return (null, root);
        }

        var section = new JsonObject();
        if (root[target.Section] is JsonObject parsedSection)
        {
            foreach (var (path, entry) in parsedSection.ToArray())
            {
                parsedSection.Remove(path);
                section[path] = entry;
            }
        }

        root[target.Section] = section;

        var key = MetadataPathKey.Normalize(target.Path!);
        string? foundPath = null;
        foreach (var (path, _) in section)
        {
            if (MetadataPathKey.Normalize(path) == key)
            {
                if (foundPath is not null)
                {
                    throw Refuse($"The metadata file has two entries for the same target, {NameList.Format([foundPath, path])}. Remove one of them, then try again.");
                }

                foundPath = path;
            }
        }

        if (foundPath is null)
        {
            var created = new JsonObject(NodeOptions);
            section[target.Path!] = created;
            return (section, created);
        }

        var found = (JsonObject)section[foundPath]!;
        if (foundPath != target.Path)
        {
            // Written with the path of the registry, whichever notation it was read in.
            section.Remove(foundPath);
            section[target.Path!] = found;
        }

        return (section, found);
    }

    private static void MergeValues(JsonObject entry, JsonElement values, bool isContainer)
    {
        var givenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in values.EnumerateObject())
        {
            string key;
            try
            {
                key = property.Name;
            }
            catch (InvalidOperationException)
            {
                throw Refuse("A key cannot be read, such as one with a lone surrogate.");
            }

            if (key.Length > MaxKeyLength)
            {
                throw Refuse($"The key {NameList.Format([key])} is longer than {MaxKeyLength} characters.");
            }

            if (!givenKeys.Add(key))
            {
                throw Refuse($"{NameList.Format([key])} is given more than once, without regard to case.");
            }

            // Before a null removes a key, so that a call with many keys is refused whatever their values.
            if (givenKeys.Count > MaxKeyCount)
            {
                throw Refuse($"values has more than {MaxKeyCount} keys.");
            }

            if (key.StartsWith('$'))
            {
                throw Refuse($"{NameList.Format([key])} is reserved: keys that start with $ are no metadata.");
            }

            var knownKey = Array.Find(KnownKeys, candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
            var isOrder = knownKey == MetadataKeys.Order;
            if (isOrder && !isContainer)
            {
                throw Refuse($"{NameList.Format([key])} is the order of the children of a folder; only a folder or the root of a storage has one.");
            }

            var value = knownKey switch
            {
                MetadataKeys.Order => ToOrder(key, property.Value),
                MetadataKeys.Location => ToName<NavigationLocation>(knownKey, property.Value),
                MetadataKeys.Alignment => ToName<AppBarAlignment>(knownKey, property.Value),
                null => ToNode(property.Value),
                _ => ToString(knownKey, property.Value)
            };

            if (value is null)
            {
                entry.Remove(key);
                continue;
            }

            var size = GetSize(key, value);

            // The order of a folder is limited by the file only, as it names every child.
            if (!isOrder && GetText(value) is { Length: > MaxStringLength })
            {
                throw Refuse($"The value of {NameList.Format([key])} is longer than {MaxStringLength} characters.");
            }

            if (!isOrder && size > MaxValueSize)
            {
                throw Refuse($"The value of {NameList.Format([key])} is larger than {MaxValueSize / 1024} KB.");
            }

            if (!entry.ContainsKey(key) && CountKeys(entry) >= MaxKeyCount)
            {
                throw Refuse($"{NameList.Format([key])} cannot be added: the metadata already has {MaxKeyCount} keys.");
            }

            // A key of the entry that differs only in case keeps its name; a new known key is written as HomeBlaze names it.
            entry[knownKey ?? key] = value;
        }
    }

    // The keys of an entry that hold a value, without the reserved ones of a file.
    private static int CountKeys(JsonObject entry)
        => entry.Count(pair => pair.Value is not null && !pair.Key.StartsWith('$'));

    private static JsonNode? ToNode(JsonElement value)
        => value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(value.GetRawText(), NodeOptions);

    // A blank string removes the key, so that a value of only spaces never shows.
    private static JsonNode? ToString(string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Refuse($"'{key}' must be a string or null.");
        }

        var text = GetString(key, value);
        return string.IsNullOrWhiteSpace(text) ? null : JsonValue.Create(text);
    }

    // By name only, without regard to case, stored as the name: the name check leaves out the numbers and the lists of
    // names that Enum.TryParse also takes.
    private static JsonNode? ToName<TEnum>(string key, JsonElement value) where TEnum : struct, Enum
    {
        if (ToString(key, value) is not { } node)
        {
            return null;
        }

        var text = node.GetValue<string>();
        return Enum.TryParse(text, ignoreCase: true, out TEnum name) && string.Equals(name.ToString(), text, StringComparison.OrdinalIgnoreCase)
            ? JsonValue.Create(name.ToString())
            : throw Refuse($"'{key}' must be one of {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    // An empty order removes the key.
    private static JsonNode? ToOrder(string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
        {
            throw Refuse($"{NameList.Format([key])} must be an array of names of children.");
        }

        var order = new JsonArray(NodeOptions);
        foreach (var item in value.EnumerateArray())
        {
            order.Add(JsonValue.Create(GetString(key, item)));
        }

        return order.Count > 0 ? order : null;
    }

    private static string? GetString(string key, JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            throw RefuseUnwritable(key);
        }
    }

    // A string with a lone surrogate cannot be read as text, which a write refuses when it writes it.
    private static string? GetText(JsonNode node)
    {
        try
        {
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int GetSize(string key, JsonNode value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CompactOptions))
        {
            try
            {
                value.WriteTo(writer);
            }
            catch (InvalidOperationException)
            {
                throw RefuseUnwritable(key);
            }
        }

        return buffer.WrittenCount;
    }

    // Indented, ending with a new line, with non-ASCII characters as they are and without the keys whose value is null.
    private static byte[] Serialize(JsonObject root)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, FileOptions))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in root)
            {
                if (value is not null)
                {
                    writer.WritePropertyName(key);
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    private static StorageRefusedException Refuse(string message) => new(message);

    private static StorageRefusedException RefuseUnwritable(string key)
        => Refuse($"The value of {NameList.Format([key])} cannot be written, such as a string with a lone surrogate.");
}

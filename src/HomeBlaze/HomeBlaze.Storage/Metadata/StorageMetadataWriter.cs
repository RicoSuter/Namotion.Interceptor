using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions.Metadata;
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Metadata;

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

    private static readonly JsonWriterOptions FileOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonWriterOptions CompactOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Merges values into the entry of the target: a key with a value is set, a nested value replacing the old one as
    /// a whole, a key with null is removed, and every other key stays. Keys are matched exactly. An entry of a subject or
    /// property is found in either notation of its path and written with <see cref="StorageMetadataTarget.Path"/>. An
    /// entry without keys is removed, and so is a section without entries.
    /// </summary>
    /// <param name="content">The content of the file, which <see cref="StorageMetadataFile.TryParse"/> accepts; null
    /// without a file.</param>
    /// <param name="target">The file and the entry.</param>
    /// <param name="values">A JSON object.</param>
    /// <returns>The content to write, indented and ending with a new line, and the file as a pass reads it; or null
    /// when the file holds no key any more and is deleted.</returns>
    /// <exception cref="StorageRefusedException">
    /// values is no JSON object, gives a key twice, has a key that starts with $, sets a known key in another case to a
    /// value, sets <c>order</c> of anything but a folder or the root, has a known key with a value of another kind, has
    /// a key that cannot be read or a value that cannot be written, or exceeds a limit; or the file has two entries of
    /// the target, cannot be read as a pass reads it, has a path twice, or cannot be written back, such as with a string
    /// with a lone surrogate; or the path of the target cannot be read back. The message names the key, the file or the
    /// path.
    /// </exception>
    public static (byte[] Content, StorageMetadataFile File)? Merge(byte[]? content, StorageMetadataTarget target, JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Object)
        {
            throw Refuse("values must be a JSON object, for example { \"navTitle\": \"Pump\" }.");
        }

        var root = new JsonObject();
        var sizeBefore = 0;
        if (content is not null)
        {
            // Read as a pass reads it first, so that a file that a pass cannot read is never written.
            if (!StorageMetadataFile.TryParse(content, out _, out _))
            {
                throw CreateUnreadableException(target.FilePath);
            }

            try
            {
                root = Parse(content);
                sizeBefore = Serialize(root).Length;
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or ArgumentException)
            {
                throw CreateUnreadableException(target.FilePath);
            }
        }

        JsonObject? section;
        JsonObject entry;
        try
        {
            (section, entry) = GetEntry(root, target);
        }
        catch (ArgumentException)
        {
            // A section with a path twice in exactly the same notation, which a pass reads as one entry.
            throw CreateUnreadableException(target.FilePath);
        }

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

        return StorageMetadataFile.TryParse(written, out var file, out var error)
            ? (written, file)
            : throw new InvalidOperationException("The merged metadata cannot be read back.", error);
    }

    /// <summary>The refusal of a write to a metadata file that cannot be read or written back.</summary>
    public static StorageRefusedException CreateUnreadableException(string path)
        => new($"The metadata file '{path}' could not be read. Fix or delete it, then try again.");

    private static JsonObject Parse(byte[] content)
    {
        return JsonNode.Parse(StorageMetadataFile.GetJson(content), documentOptions: StorageMetadataFile.DocumentOptions) as JsonObject
            ?? throw new JsonException("A metadata file holds a JSON object.");
    }

    // The section and the entry of the target in it, or no section and the file itself for its own keys.
    private static (JsonObject? Section, JsonObject Entry) GetEntry(JsonObject root, StorageMetadataTarget target)
    {
        if (target.Section is null)
        {
            return (null, root);
        }

        var key = MetadataPathKey.Normalize(target.Path!)
            ?? throw Refuse($"The path {NameList.Format([target.Path!])} cannot be written to a metadata file, as it cannot be read back.");
        if (root[target.Section] is not JsonObject section)
        {
            section = new JsonObject();
            root[target.Section] = section;
        }

        var foundIndex = -1;
        for (var index = 0; index < section.Count; index++)
        {
            if (MetadataPathKey.Normalize(section.GetAt(index).Key) == key)
            {
                if (foundIndex >= 0)
                {
                    throw Refuse($"The metadata file has two entries for the same target, {NameList.Format([section.GetAt(foundIndex).Key, section.GetAt(index).Key])}. Remove one of them, then try again.");
                }

                foundIndex = index;
            }
        }

        if (foundIndex < 0)
        {
            var created = new JsonObject();
            section[target.Path!] = created;
            return (section, created);
        }

        // Written with the path of the registry in its place, whichever notation it was read in.
        var (foundPath, foundEntry) = section.GetAt(foundIndex);
        var found = (JsonObject)foundEntry!;
        if (foundPath != target.Path)
        {
            section.RemoveAt(foundIndex);
            section.Insert(foundIndex, target.Path!, found);
        }

        return (section, found);
    }

    private static void MergeValues(JsonObject entry, JsonElement values, bool isContainer)
    {
        var givenKeys = new HashSet<string>();
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
                throw Refuse($"{NameList.Format([key])} is given more than once.");
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

            var knownKey = Array.IndexOf(KnownKeys, key) >= 0 ? key : null;
            if (knownKey is null && property.Value.ValueKind != JsonValueKind.Null &&
                Array.Find(KnownKeys, candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)) is { } canonicalKey)
            {
                throw Refuse($"Keys match exactly, so {NameList.Format([key])} would not be read: use {NameList.Format([canonicalKey])}.");
            }

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
            if (!isOrder && property.Value.ValueKind == JsonValueKind.String && GetString(key, property.Value) is { Length: > MaxStringLength })
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

            entry[key] = value;
        }
    }

    // The keys of an entry that hold a value, without the reserved ones of a file.
    private static int CountKeys(JsonObject entry)
        => entry.Count(pair => pair.Value is not null && !pair.Key.StartsWith('$'));

    private static JsonNode? ToNode(JsonElement value)
        => value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(value.GetRawText());

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

    // By name only, without regard to case, stored as the name.
    private static JsonNode? ToName<TEnum>(string key, JsonElement value) where TEnum : struct, Enum
    {
        if (ToString(key, value) is not { } node)
        {
            return null;
        }

        return MetadataDictionaryExtensions.ParseEnumName<TEnum>(node.GetValue<string>()) is { } name
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

        var order = new JsonArray();
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

    // Indented, ending with a new line, and without the keys whose value is null. The built-in relaxed encoder writes most
    // characters as they are and escapes a few: characters outside the basic plane such as emoji, U+2028, U+2029, U+FEFF,
    // and private-use and unassigned characters.
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

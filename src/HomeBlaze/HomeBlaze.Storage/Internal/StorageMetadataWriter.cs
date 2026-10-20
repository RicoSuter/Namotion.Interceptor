using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;

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

    /// <summary>The most links and tags.</summary>
    public const int MaxItemCount = 20;

    /// <summary>The longest tag.</summary>
    public const int MaxTagLength = 64;

    private static readonly string[] KnownKeys =
    [
        MetadataKeys.Title, MetadataKeys.NavigationTitle, MetadataKeys.Icon, MetadataKeys.Location, MetadataKeys.Alignment,
        MetadataKeys.Order, MetadataKeys.Notes, MetadataKeys.Links, MetadataKeys.Tags
    ];

    private static readonly string[] LinkMembers = ["title", "url", "path"];

    // The keys of the file, its own included, and of an entry are matched without regard to case.
    private static readonly JsonNodeOptions KeyOptions = new() { PropertyNameCaseInsensitive = true };

    // The paths of a section and the keys inside a value are matched with regard to case. Set on every other object, as
    // an object without options takes those of its parent.
    private static readonly JsonNodeOptions CaseSensitiveOptions = new();

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
    /// <param name="content">The content of the file, which <see cref="StorageMetadataFile.TryParse"/> accepts; null
    /// without a file.</param>
    /// <param name="target">The file and the entry.</param>
    /// <param name="values">A JSON object.</param>
    /// <returns>The content to write, indented and ending with a new line, and the file as a pass reads it; or null
    /// when the file holds no key any more and is deleted.</returns>
    /// <exception cref="StorageRefusedException">
    /// values is no JSON object, gives a key twice without regard to case, has a key that starts with $, sets
    /// <c>order</c> of anything but a folder or the root, has a known key with a value of another kind, has a key that
    /// cannot be read or a value that cannot be written, or exceeds a limit; or the file has two entries of the target,
    /// cannot be read as a pass reads it, has a path twice, or cannot be written back, such as with a string with a lone
    /// surrogate. The message names the key or the file.
    /// </exception>
    public static (byte[] Content, StorageMetadataFile File)? Merge(byte[]? content, StorageMetadataTarget target, JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Object)
        {
            throw Refuse("values must be a JSON object, for example { \"navTitle\": \"Pump\" }.");
        }

        var root = new JsonObject(KeyOptions);
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
        ReadOnlySpan<byte> json = content;
        if (json.StartsWith(Encoding.UTF8.Preamble))
        {
            json = json[Encoding.UTF8.Preamble.Length..];
        }

        var root = JsonNode.Parse(json, CaseSensitiveOptions, StorageMetadataFile.DocumentOptions) as JsonObject
            ?? throw new JsonException("A metadata file holds a JSON object.");
        return WithKeysWithoutCase(root);
    }

    // The properties of the object, moved to one whose keys are matched without regard to case.
    private static JsonObject WithKeysWithoutCase(JsonObject source)
    {
        var properties = source.ToArray();
        source.Clear();
        return new JsonObject(properties, KeyOptions);
    }

    // The section and the entry of the target in it, or no section and the file itself for its own keys.
    private static (JsonObject? Section, JsonObject Entry) GetEntry(JsonObject root, StorageMetadataTarget target)
    {
        if (target.Section is null)
        {
            return (null, root);
        }

        if (root[target.Section] is not JsonObject section)
        {
            section = new JsonObject(CaseSensitiveOptions);
            root[target.Section] = section;
        }

        var key = MetadataPathKey.Normalize(target.Path!);
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
            var created = new JsonObject(KeyOptions);
            section[target.Path!] = created;
            return (section, created);
        }

        // Written with the path of the registry in its place, whichever notation it was read in.
        var found = WithKeysWithoutCase((JsonObject)section.GetAt(foundIndex).Value!);
        section.SetAt(foundIndex, target.Path!, found);
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
                MetadataKeys.Notes => ToNotes(knownKey, property.Value),
                MetadataKeys.Links => ToLinks(knownKey, property.Value),
                MetadataKeys.Tags => ToTags(knownKey, property.Value),
                null => ToNode(property.Value),
                _ => ToString(knownKey, property.Value)
            };

            if (value is null)
            {
                entry.Remove(key);
                continue;
            }

            var size = GetSize(key, value);

            // The order of a folder is limited by the file only, as it names every child, and notes by their own limit.
            var hasOwnLimit = isOrder || knownKey == MetadataKeys.Notes;
            if (!hasOwnLimit && property.Value.ValueKind == JsonValueKind.String && GetString(key, property.Value) is { Length: > MaxStringLength })
            {
                throw Refuse($"The value of {NameList.Format([key])} is longer than {MaxStringLength} characters.");
            }

            if (!hasOwnLimit && size > MaxValueSize)
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
        => value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(value.GetRawText(), CaseSensitiveOptions);

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

    // A blank text removes the key.
    private static JsonNode? ToNotes(string key, JsonElement value)
    {
        if (ToString(key, value) is not { } node)
        {
            return null;
        }

        return MetadataNotes.GetError(node.GetValue<string>()) is { } error ? throw Refuse(error) : node;
    }

    // An empty array removes the key. Each link is an object with an optional title and exactly one of an absolute
    // http, https or mailto url and an absolute path.
    private static JsonNode? ToLinks(string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxItemCount)
        {
            throw Refuse($"'{key}' must be an array of at most {MaxItemCount} links, for example [{{ \"title\": \"Manual\", \"url\": \"https://example.com/manual.pdf\" }}].");
        }

        var links = new JsonArray(CaseSensitiveOptions);
        foreach (var item in value.EnumerateArray())
        {
            links.Add(ToLink(key, item));
        }

        return links.Count > 0 ? links : null;
    }

    private static JsonObject ToLink(string key, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw Refuse($"Each item of '{key}' must be an object with a title and a url or a path.");
        }

        var link = new JsonObject(CaseSensitiveOptions);
        foreach (var member in item.EnumerateObject())
        {
            var name = Array.Find(LinkMembers, candidate => string.Equals(candidate, member.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw Refuse($"An item of '{key}' has {NameList.Format([member.Name])}; a link has only a title and a url or a path.");
            if (link.ContainsKey(name))
            {
                throw Refuse($"An item of '{key}' gives {NameList.Format([name])} more than once, without regard to case.");
            }

            if (ToString(key, member.Value) is not { } text)
            {
                continue;
            }

            if (text.GetValue<string>().Length > MaxStringLength)
            {
                throw Refuse($"The {name} of an item of '{key}' is longer than {MaxStringLength} characters.");
            }

            link[name] = text;
        }

        var url = (string?)link["url"];
        var path = (string?)link["path"];
        if ((url is null) == (path is null))
        {
            throw Refuse($"Each item of '{key}' has exactly one of a url and a path.");
        }

        if (url is not null && !MetadataLink.IsWebAddress(url))
        {
            throw Refuse($"The url {NameList.Format([url])} of '{key}' is no absolute http, https or mailto address.");
        }

        if (path is not null && !path.StartsWith('/'))
        {
            throw Refuse($"The path {NameList.Format([path])} of '{key}' is no absolute path, such as /Devices/Pump.");
        }

        return link;
    }

    // An empty array removes the key. Tags are trimmed.
    private static JsonNode? ToTags(string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxItemCount ||
            value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
        {
            throw Refuse($"'{key}' must be an array of at most {MaxItemCount} strings.");
        }

        var tags = new JsonArray(CaseSensitiveOptions);
        var givenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            var tag = GetString(key, item)!.Trim();
            if (tag.Length == 0 || tag.Length > MaxTagLength)
            {
                throw Refuse($"Each tag of '{key}' has 1 to {MaxTagLength} characters.");
            }

            if (!givenTags.Add(tag))
            {
                throw Refuse($"The tag {NameList.Format([tag])} is given more than once, without regard to case.");
            }

            tags.Add(JsonValue.Create(tag));
        }

        return tags.Count > 0 ? tags : null;
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

        var order = new JsonArray(CaseSensitiveOptions);
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

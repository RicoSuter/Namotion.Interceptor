using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Components.Abstractions.Pages;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// What a metadata file describes, which decides the keys HomeBlaze knows in it.
/// </summary>
internal enum StorageMetadataKind
{
    /// <summary>The <c>.metadata.json</c> of a folder or of the root: title, navTitle, icon and the order.</summary>
    Folder,

    /// <summary>The <c>X.metadata.json</c> beside <c>X.json</c>: navTitle, icon, location and alignment.</summary>
    Subject
}

/// <summary>
/// The content of a metadata file: every key it holds, matched without regard to case. Keys that HomeBlaze does not
/// know are kept as they are, so that writing the file does not remove them.
/// </summary>
/// <remarks>
/// Not thread-safe. A write changes only a document that it has just read from the storage, and drops it when a
/// merge is refused, as the merge may have changed it in part.
/// </remarks>
internal sealed class StorageMetadataDocument
{
    private const int MaxKeyCount = 64;
    private const int MaxKeyLength = 64;
    private const int MaxStringLength = 256;
    private const int MaxValueSize = 4 * 1024;
    private const int MaxFileSize = 16 * 1024;

    private static readonly string[] FolderKeys = [MetadataKeys.Title, MetadataKeys.NavigationTitle, MetadataKeys.Icon];

    private static readonly string[] SubjectKeys =
        [MetadataKeys.NavigationTitle, MetadataKeys.Icon, MetadataKeys.Location, MetadataKeys.Alignment];

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonWriterOptions FileOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonWriterOptions CompactOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly JsonObject _root;

    public StorageMetadataDocument()
        : this(new JsonObject(NodeOptions))
    {
    }

    private StorageMetadataDocument(JsonObject root)
    {
        _root = root;
    }

    /// <summary>True when no key has a value, so that the file is deleted rather than written as <c>{}</c>.</summary>
    public bool IsEmpty
    {
        get
        {
            foreach (var (_, value) in _root)
            {
                if (value is not null)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Reads the content of a metadata file. Content that is no JSON object, or that has a key that cannot be read,
    /// such as one with a lone surrogate or one given twice without regard to case, is no metadata.
    /// </summary>
    public static bool TryParse(
        byte[] content, [NotNullWhen(true)] out StorageMetadataDocument? document, [NotNullWhen(false)] out Exception? error)
    {
        ReadOnlySpan<byte> json = content;
        if (json.StartsWith(Encoding.UTF8.Preamble))
        {
            json = json[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            if (JsonNode.Parse(json, NodeOptions, DocumentOptions) is not JsonObject root)
            {
                throw new JsonException("A metadata file holds a JSON object.");
            }

            // Reads the keys here, which the node otherwise reads when it is first used, by a pass that applies it.
            _ = root.Count;
            document = new StorageMetadataDocument(root);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            document = null;
            error = exception;
            return false;
        }
    }

    /// <summary>The refusal of a Get or a write of a metadata file that could not be read.</summary>
    public static InvalidOperationException CreateUnreadableException(string path)
        => new($"The metadata file '{path}' could not be read. Fix or delete it, then try again.");

    /// <summary>
    /// The content of the file: indented, ending with a new line, with non-ASCII characters as they are and without
    /// the keys whose value is null.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value cannot be written, such as a string with a lone surrogate.</exception>
    public byte[] Serialize()
    {
        var buffer = new ArrayBufferWriter<byte>();
        Write(buffer, FileOptions, includesOrder: true);
        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Gets every key as a JSON object, without <c>order</c> unless it is asked for.</summary>
    public JsonElement ToJsonElement(bool includesOrder)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Write(buffer, CompactOptions, includesOrder);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public FolderMetadata? ToFolderMetadata()
    {
        var metadata = new FolderMetadata
        {
            Title = GetString(MetadataKeys.Title),
            NavigationTitle = GetString(MetadataKeys.NavigationTitle),
            Icon = GetString(MetadataKeys.Icon)
        };
        return metadata is { Title: null, NavigationTitle: null, Icon: null } ? null : metadata;
    }

    public SubjectMetadata? ToSubjectMetadata()
    {
        var metadata = new SubjectMetadata
        {
            NavigationTitle = GetString(MetadataKeys.NavigationTitle),
            Icon = GetString(MetadataKeys.Icon),
            Location = GetEnum<NavigationLocation>(MetadataKeys.Location),
            Alignment = GetEnum<AppBarAlignment>(MetadataKeys.Alignment)
        };
        return metadata is { NavigationTitle: null, Icon: null, Location: null, Alignment: null } ? null : metadata;
    }

    /// <summary>Gets the names of <c>order</c>, leaving out the values that are no string.</summary>
    public ImmutableArray<string> ToChildOrder()
    {
        if (_root[MetadataKeys.Order] is not JsonArray order)
        {
            return [];
        }

        var names = ImmutableArray.CreateBuilder<string>(order.Count);
        foreach (var item in order)
        {
            if (GetText(item) is { } name)
            {
                names.Add(name);
            }
        }

        return names.ToImmutable();
    }

    /// <summary>Sets <c>order</c> to the names, and removes it for none.</summary>
    public void SetOrder(IEnumerable<string> names)
    {
        var order = new JsonArray(NodeOptions);
        foreach (var name in names)
        {
            order.Add(JsonValue.Create(name));
        }

        if (order.Count == 0)
        {
            _root.Remove(MetadataKeys.Order);
        }
        else
        {
            _root[MetadataKeys.Order] = order;
        }
    }

    /// <summary>
    /// Merges values into the document at the top level: a key with a value is set, a nested value replacing the
    /// old one, a key with null is removed, and every other key stays. A key matches the key of the file that differs
    /// only in case, whose name stays. A known key takes a string, an enum key one of its names, and a blank string
    /// removes it. Unknown keys are stored as given.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// values is no JSON object, gives a key twice without regard to case, sets <c>order</c>, has a known key with a
    /// value of another kind, or exceeds a limit: 64 keys, key names of 64 characters, strings of 256 characters,
    /// values of 4 KB and a whole file of 16 KB. The message names the key.
    /// </exception>
    /// <exception cref="InvalidOperationException">A value cannot be written, such as a string with a lone surrogate.</exception>
    public void Merge(JsonElement values, StorageMetadataKind kind)
    {
        if (values.ValueKind != JsonValueKind.Object)
        {
            throw Refuse("values must be a JSON object, for example { \"navTitle\": \"Pump\" }.");
        }

        var knownKeys = kind == StorageMetadataKind.Folder ? FolderKeys : SubjectKeys;
        var givenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in values.EnumerateObject())
        {
            var key = property.Name;
            if (!givenKeys.Add(key))
            {
                throw Refuse($"'{key}' is given more than once, without regard to case.");
            }

            if (key.Length > MaxKeyLength)
            {
                throw Refuse($"The key '{key[..MaxKeyLength]}' is longer than {MaxKeyLength} characters.");
            }

            if (string.Equals(key, MetadataKeys.Order, StringComparison.OrdinalIgnoreCase))
            {
                throw Refuse($"'{key}' cannot be set here; use Set Order.");
            }

            var knownKey = Array.Find(knownKeys, candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
            var value = knownKey is null ? ToNode(property.Value) : ToKnownValue(knownKey, property.Value);
            if (value is null)
            {
                _root.Remove(key);
                continue;
            }

            if (GetText(value) is { Length: > MaxStringLength })
            {
                throw Refuse($"The value of '{key}' is longer than {MaxStringLength} characters.");
            }

            if (GetSize(value) > MaxValueSize)
            {
                throw Refuse($"The value of '{key}' is larger than {MaxValueSize / 1024} KB.");
            }

            if (!_root.ContainsKey(key) && _root.Count >= MaxKeyCount)
            {
                throw Refuse($"'{key}' cannot be added: the metadata already has {MaxKeyCount} keys.");
            }

            _root[knownKey ?? key] = value;
        }

        if (Serialize().Length > MaxFileSize)
        {
            throw Refuse($"The metadata file would be larger than {MaxFileSize / 1024} KB with {string.Join(", ", givenKeys.Select(key => $"'{key}'"))}.");
        }
    }

    private void Write(IBufferWriter<byte> buffer, JsonWriterOptions options, bool includesOrder)
    {
        using var writer = new Utf8JsonWriter(buffer, options);
        writer.WriteStartObject();
        foreach (var (key, value) in _root)
        {
            if (value is null || (!includesOrder && string.Equals(key, MetadataKeys.Order, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            writer.WritePropertyName(key);
            value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static int GetSize(JsonNode value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CompactOptions))
        {
            value.WriteTo(writer);
        }

        return buffer.WrittenCount;
    }

    // A copy, as the element belongs to the document of the caller.
    private static JsonNode? ToNode(JsonElement value)
        => value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(value.GetRawText(), NodeOptions);

    private static JsonNode? ToKnownValue(string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Refuse($"'{key}' must be a string or null.");
        }

        var text = GetValue(value.GetString());
        return text is null ? null : JsonValue.Create(key switch
        {
            MetadataKeys.Location => GetName<NavigationLocation>(key, text),
            MetadataKeys.Alignment => GetName<AppBarAlignment>(key, text),
            _ => text
        });
    }

    private static string GetName<TEnum>(string key, string text) where TEnum : struct, Enum
        => TryParseName(text, out TEnum value)
            ? value.ToString()
            : throw Refuse($"'{key}' must be one of {string.Join(", ", Enum.GetNames<TEnum>())}.");

    private string? GetString(string key) => GetValue(GetText(_root[key]));

    private TEnum? GetEnum<TEnum>(string key) where TEnum : struct, Enum
        => GetString(key) is { } text && TryParseName(text, out TEnum value) ? value : null;

    // By name only, without regard to case: Enum.TryParse would also take numbers and lists of names.
    private static bool TryParseName<TEnum>(string text, out TEnum value) where TEnum : struct, Enum
    {
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                value = Enum.Parse<TEnum>(name);
                return true;
            }
        }

        value = default;
        return false;
    }

    // A string with a lone surrogate cannot be read as text, which counts as no value.
    private static string? GetText(JsonNode? node)
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

    // Blank means not set, so that a value of only spaces never shows and a write of one removes the key.
    private static string? GetValue(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static ArgumentException Refuse(string message) => new(message, "values");
}

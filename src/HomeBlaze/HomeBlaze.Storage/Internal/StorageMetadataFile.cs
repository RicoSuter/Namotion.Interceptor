using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The content of a metadata file as it applies: the own keys, and the entries of <see cref="SubjectsKey"/> and
/// <see cref="PropertiesKey"/> by their key (see <see cref="MetadataPathKey"/>). It holds paths and JSON values,
/// never subjects. Keys and paths are matched exactly.
/// </summary>
/// <remarks>Immutable, so that every thread reads it once it is published.</remarks>
internal sealed class StorageMetadataFile
{
    /// <summary>The key of the entries of the subjects inside the stored subject, by their path.</summary>
    public const string SubjectsKey = "$subjects";

    /// <summary>The key of the entries of the properties of the stored subject and of the subjects inside it, by their path.</summary>
    public const string PropertiesKey = "$properties";

    private const string TypeKey = "$type";

    /// <summary>Comments and a trailing comma are allowed when a file is read.</summary>
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Dictionary<string, ImmutableDictionary<string, JsonElement>> NoEntries = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ImmutableDictionary<string, JsonElement>> _subjects;
    private readonly Dictionary<string, ImmutableDictionary<string, JsonElement>> _properties;
    private ImmutableArray<string> _order;

    private StorageMetadataFile(
        ImmutableDictionary<string, JsonElement>? own,
        Dictionary<string, ImmutableDictionary<string, JsonElement>> subjects,
        Dictionary<string, ImmutableDictionary<string, JsonElement>> properties,
        IReadOnlyList<string> duplicatePaths,
        bool holdsSubjectType)
    {
        Own = own;
        _subjects = subjects;
        _properties = properties;
        DuplicatePaths = duplicatePaths;
        HoldsSubjectType = holdsSubjectType;
    }

    /// <summary>Gets the content of a file without keys, which stands for no file.</summary>
    public static StorageMetadataFile Empty { get; } = new(null, NoEntries, NoEntries, [], false);

    /// <summary>Gets the own keys of the file, those of the stored subject, the folder or the root; null without one.</summary>
    public ImmutableDictionary<string, JsonElement>? Own { get; }

    /// <summary>
    /// Gets the names of the <c>order</c> of the own keys, of a folder or of the root, read once; empty without one.
    /// </summary>
    public ImmutableArray<string> Order
    {
        get
        {
            // Threads that race each read equal arrays; an ImmutableArray is a single reference, so a read is never torn.
            if (_order.IsDefault)
            {
                _order = Own.GetStringArray(MetadataKeys.Order);
            }

            return _order;
        }
    }

    /// <summary>Gets whether the file has an entry of a subject.</summary>
    public bool HasSubjects => _subjects.Count > 0;

    /// <summary>Gets whether the file has an entry of a property.</summary>
    public bool HasProperties => _properties.Count > 0;

    /// <summary>Gets the paths of the entries that have the key of an entry before them, which do not apply.</summary>
    public IReadOnlyList<string> DuplicatePaths { get; }

    /// <summary>Gets whether the file has a <c>$type</c>, as the file of a subject has.</summary>
    public bool HoldsSubjectType { get; }

    /// <summary>Gets the entry of the subject with the key; null without one.</summary>
    public ImmutableDictionary<string, JsonElement>? FindSubject(string key) => _subjects.GetValueOrDefault(key);

    /// <summary>Gets the entry of the property with the key; null without one.</summary>
    public ImmutableDictionary<string, JsonElement>? FindProperty(string key) => _properties.GetValueOrDefault(key);

    /// <summary>
    /// Reads the content of a metadata file. Content that is no JSON object, has a key that cannot be read, such as one
    /// with a lone surrogate, has a key of the file or of an entry twice, or has a section or an entry that is
    /// no JSON object or a path that cannot be read, is no metadata. A key with null is left out, and so is an entry
    /// without keys.
    /// </summary>
    public static bool TryParse(
        byte[] content, [NotNullWhen(true)] out StorageMetadataFile? file, [NotNullWhen(false)] out Exception? error)
    {
        ReadOnlySpan<byte> json = content;
        if (json.StartsWith(Encoding.UTF8.Preamble))
        {
            json = json[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            var root = JsonElement.Parse(json, DocumentOptions);
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A metadata file holds a JSON object.");
            }

            var reservedKeys = new List<JsonProperty>();
            var own = ReadValues(root, reservedKeys, cloneValues: true);
            var subjects = NoEntries;
            var properties = NoEntries;
            List<string>? duplicatePaths = null;
            var holdsSubjectType = false;
            foreach (var reservedKey in reservedKeys)
            {
                if (reservedKey.Name == SubjectsKey)
                {
                    subjects = ReadEntries(reservedKey.Value, ref duplicatePaths);
                }
                else if (reservedKey.Name == PropertiesKey)
                {
                    properties = ReadEntries(reservedKey.Value, ref duplicatePaths);
                }
                else if (reservedKey.Name == TypeKey)
                {
                    holdsSubjectType = true;
                }
            }

            file = new StorageMetadataFile(own, subjects, properties, duplicatePaths ?? [], holdsSubjectType);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            file = null;
            error = exception;
            return false;
        }
    }

    // The keys of an object that hold a value; null without one. The reserved keys, which start with $, go to
    // reservedKeys, or are left out without it. A value refers to the document it was read from, which an unchanged
    // value that is kept when the file is read again keeps alive: the values of the root are cloned each, and an entry
    // is cloned as a whole before it is read, so that neither keeps the document of the whole file.
    private static ImmutableDictionary<string, JsonElement>? ReadValues(JsonElement entry, List<JsonProperty>? reservedKeys, bool cloneValues)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An entry of a metadata file holds a JSON object.");
        }

        var values = ImmutableDictionary.CreateBuilder<string, JsonElement>();
        var names = new HashSet<string>();
        foreach (var property in entry.EnumerateObject())
        {
            // Throws for a name with a lone surrogate.
            var name = property.Name;
            if (!names.Add(name))
            {
                throw new JsonException($"The key {NameList.Format([name])} is given twice.");
            }

            if (name.StartsWith('$'))
            {
                reservedKeys?.Add(property);
            }
            else if (property.Value.ValueKind != JsonValueKind.Null)
            {
                values[name] = cloneValues ? property.Value.Clone() : property.Value;
            }
        }

        return values.Count > 0 ? values.ToImmutable() : null;
    }

    private static Dictionary<string, ImmutableDictionary<string, JsonElement>> ReadEntries(
        JsonElement section, ref List<string>? duplicatePaths)
    {
        if (section.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{SubjectsKey} and {PropertiesKey} hold JSON objects.");
        }

        var entries = new Dictionary<string, ImmutableDictionary<string, JsonElement>>(StringComparer.Ordinal);

        // With the keys of the entries without keys, which are left out but still come first.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in section.EnumerateObject())
        {
            var path = entry.Name;
            var key = MetadataPathKey.Normalize(path)
                ?? throw new JsonException($"The path {NameList.Format([path])} cannot be read.");
            var values = ReadValues(entry.Value.Clone(), reservedKeys: null, cloneValues: false);
            if (!keys.Add(key))
            {
                (duplicatePaths ??= []).Add(path);
            }
            else if (values is not null)
            {
                entries.Add(key, values);
            }
        }

        return entries;
    }
}

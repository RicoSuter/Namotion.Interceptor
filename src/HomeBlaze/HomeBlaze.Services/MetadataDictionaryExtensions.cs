using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HomeBlaze.Storage.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Reads the keys of metadata, a dictionary of key to JSON value, see <c>MetadataKeys</c>. A value of another shape
/// than the key takes counts as not set.
/// </summary>
public static class MetadataDictionaryExtensions
{
    /// <summary>Gets the string of the key; null when it is not set, no string, blank or cannot be read.</summary>
    public static string? GetString(this IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = GetText(value);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Gets the value of the enum that the string of the key names, without regard to case; null for anything else,
    /// a number included.
    /// </summary>
    public static TEnum? GetEnum<TEnum>(this IReadOnlyDictionary<string, JsonElement>? metadata, string key)
        where TEnum : struct, Enum
        => metadata.GetString(key) is { } text ? ParseEnumName<TEnum>(text) : null;

    /// <summary>Gets the value of the enum that the text names, without regard to case; null for anything else, a number included.</summary>
    public static TEnum? ParseEnumName<TEnum>(string text)
        where TEnum : struct, Enum
        => IsName(text) && Enum.TryParse(text, ignoreCase: true, out TEnum value) ? value : null;

    /// <summary>Gets the strings of the array of the key, leaving out the items that are no string; empty without an array.</summary>
    public static ImmutableArray<string> GetStringArray(this IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = ImmutableArray.CreateBuilder<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && GetText(item) is { } text)
            {
                items.Add(text);
            }
        }

        return items.DrainToImmutable();
    }

    /// <summary>
    /// Gets the tags of the first 20 items of <c>tags</c>, without whitespace around them and each once without regard to
    /// case, leaving out the items that are no string or that have no 1 to 64 characters or a control or format character.
    /// </summary>
    public static ImmutableArray<string> GetTags(this IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(MetadataKeys.Tags, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tags = ImmutableArray.CreateBuilder<string>();
        foreach (var item in value.EnumerateArray().Take(MetadataText.MaxItemCount))
        {
            if (GetTag(item) is { } tag && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(tag);
            }
        }

        return tags.DrainToImmutable();
    }

    // The result of HasNotesLinksOrTags per immutable value, as the rows of the browser check it every render.
    private static readonly ConditionalWeakTable<ImmutableDictionary<string, JsonElement>, object> NotesLinksOrTagsCache = new();
    private static readonly object True = true;
    private static readonly object False = false;

    /// <summary>
    /// Gets whether the metadata has notes, links or tags to show: notes that are not blank, or a link or a tag as
    /// <see cref="GetLinks"/> and <see cref="GetTags"/> read them.
    /// </summary>
    public static bool HasNotesLinksOrTags(this IReadOnlyDictionary<string, JsonElement>? metadata)
        => metadata is ImmutableDictionary<string, JsonElement> immutableMetadata
            ? ReferenceEquals(NotesLinksOrTagsCache.GetValue(immutableMetadata, static value => HasNotesLinksOrTagsUncached(value) ? True : False), True)
            : metadata is not null && HasNotesLinksOrTagsUncached(metadata);

    private static bool HasNotesLinksOrTagsUncached(IReadOnlyDictionary<string, JsonElement> metadata)
        => HasItem(metadata, MetadataKeys.Tags, static item => GetTag(item) is not null) ||
           HasItem(metadata, MetadataKeys.Links, static item => TryGetTarget(item, out _, out _)) ||
           metadata.GetString(MetadataKeys.Notes) is not null;

    // Whether one of the first 20 items of the array of the key is one, without the arrays of GetLinks and GetTags.
    private static bool HasItem(IReadOnlyDictionary<string, JsonElement> metadata, string key, Func<JsonElement, bool> isItem)
    {
        if (!metadata.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (count++ == MetadataText.MaxItemCount)
            {
                return false;
            }

            if (isItem(item))
            {
                return true;
            }
        }

        return false;
    }

    // The item as a tag, trimmed; null when it is no string or breaks the rules of a tag.
    private static string? GetTag(JsonElement item)
        => item.ValueKind == JsonValueKind.String &&
           GetText(item)?.Trim() is { Length: > 0 and <= MetadataText.MaxTagLength } tag &&
           !MetadataText.HasControlOrFormatCharacter(tag)
            ? tag
            : null;

    // The url and the path of the item as a link: an object with exactly one of them, without a control or format
    // character, as a link without title shows it as text.
    private static bool TryGetTarget(JsonElement item, out string? url, out string? path)
    {
        url = null;
        path = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        url = GetMember(item, "url"u8);
        path = GetMember(item, "path"u8);
        return (url is null) != (path is null) && !MetadataText.HasControlOrFormatCharacter(url ?? path!);
    }

    /// <summary>
    /// Gets the links of the first 20 items of <c>links</c>, leaving out an item that is no object with exactly one of a
    /// <c>url</c> and a <c>path</c> string, or whose url or path has a control or format character; empty without an
    /// array. The members are matched by their exact names and returned without whitespace around them, a title with a
    /// control or format character as none, and the address of a link otherwise as it is held, see <see cref="MetadataLink.IsAllowedAddress"/>.
    /// </summary>
    public static ImmutableArray<MetadataLink> GetLinks(this IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(MetadataKeys.Links, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var links = ImmutableArray.CreateBuilder<MetadataLink>();
        foreach (var item in value.EnumerateArray().Take(MetadataText.MaxItemCount))
        {
            if (TryGetTarget(item, out var url, out var path))
            {
                var title = GetMember(item, "title"u8);
                links.Add(new MetadataLink(title is not null && MetadataText.HasControlOrFormatCharacter(title) ? null : title, url, path));
            }
        }

        return links.DrainToImmutable();
    }

    // The string of a member, by its exact name, trimmed as a write trims it; null when it is not set, no string or blank.
    private static string? GetMember(JsonElement item, ReadOnlySpan<byte> utf8Name)
    {
        foreach (var member in item.EnumerateObject())
        {
            if (member.Value.ValueKind == JsonValueKind.String && HasName(member, utf8Name))
            {
                var text = GetText(member.Value)?.Trim();
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }

        return null;
    }

    // Whether the member has exactly the name; a name that cannot be read, such as one with a lone surrogate, has none.
    private static bool HasName(JsonProperty member, ReadOnlySpan<byte> utf8Name)
    {
        try
        {
            return member.NameEquals(utf8Name);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // Enum.TryParse also reads numbers, lists of names and names with spaces around them.
    private static bool IsName(string text)
    {
        if (text.Length == 0 || char.IsDigit(text[0]))
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    // A string with a lone surrogate cannot be read as text, which counts as no value.
    private static string? GetText(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using HomeBlaze.Components.Abstractions.Pages;

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

    /// <summary>
    /// Gets whether the metadata has notes, links or tags to show: notes that are not blank, or a link or a tag as
    /// <see cref="GetLinks"/> and <see cref="GetTags"/> read them.
    /// </summary>
    public static bool HasNotesLinksOrTags(this IReadOnlyDictionary<string, JsonElement>? metadata)
        => metadata is not null &&
           (HasItem(metadata, MetadataKeys.Tags, IsTag) ||
            HasItem(metadata, MetadataKeys.Links, IsLink) ||
            metadata.TryGetValue(MetadataKeys.Notes, out var notes) && HasText(notes));

    // The checks below read the raw UTF-8 of a string without escapes rather than the string, as the rows of the browser
    // check every render; they apply the rules of GetString, GetTag and TryGetTarget.

    // Whether one of the first 20 items of the array of the key is one.
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

    private static bool HasText(JsonElement value)
        => value.ValueKind == JsonValueKind.String &&
           (TryGetUtf8Text(value, out var text) ? !TrimWhiteSpace(text).IsEmpty : !string.IsNullOrWhiteSpace(GetText(value)));

    private static bool IsTag(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!TryGetUtf8Text(item, out var text))
        {
            return GetTag(item) is not null;
        }

        var tag = TrimWhiteSpace(text);
        return !tag.IsEmpty && IsAllowedText(tag, MetadataText.MaxTagLength);
    }

    private static bool IsLink(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement? url = null;
        JsonElement? path = null;
        foreach (var member in item.EnumerateObject())
        {
            if (member.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (url is null && HasName(member, "url"u8, "url"))
            {
                url = member.Value;
            }
            else if (path is null && HasName(member, "path"u8, "path"))
            {
                path = member.Value;
            }
        }

        return IsAllowedTarget(url) is { } isUrlAllowed
            ? isUrlAllowed && IsAllowedTarget(path) is null
            : IsAllowedTarget(path) == true;
    }

    // Null for a member that is not set or blank, else whether it has no control or format character.
    private static bool? IsAllowedTarget(JsonElement? member)
    {
        if (member is not { } value)
        {
            return null;
        }

        if (!TryGetUtf8Text(value, out var text))
        {
            var target = GetText(value)?.Trim();
            return string.IsNullOrEmpty(target) ? null : !MetadataText.HasControlOrFormatCharacter(target);
        }

        text = TrimWhiteSpace(text);
        return text.IsEmpty ? null : IsAllowedText(text, int.MaxValue);
    }

    // The text of a string as the raw UTF-8 between its quotes; false when it has escapes or is no valid UTF-8, as only
    // GetString reads it then.
    private static bool TryGetUtf8Text(JsonElement value, out ReadOnlySpan<byte> text)
    {
        text = JsonMarshal.GetRawUtf8Value(value)[1..^1];
        return !text.Contains((byte)'\\') && Utf8.IsValid(text);
    }

    // As string.Trim, which trims the characters that are whitespace, none of which is outside the basic plane.
    private static ReadOnlySpan<byte> TrimWhiteSpace(ReadOnlySpan<byte> text)
    {
        while (Rune.DecodeFromUtf8(text, out var character, out var size) == OperationStatus.Done && Rune.IsWhiteSpace(character))
        {
            text = text[size..];
        }

        while (Rune.DecodeLastFromUtf8(text, out var character, out var size) == OperationStatus.Done && Rune.IsWhiteSpace(character))
        {
            text = text[..^size];
        }

        return text;
    }

    // Whether the text has at most the length, in characters of UTF-16, and no control or format character.
    private static bool IsAllowedText(ReadOnlySpan<byte> text, int maxLength)
    {
        var length = 0;
        while (Rune.DecodeFromUtf8(text, out var character, out var size) == OperationStatus.Done)
        {
            length += character.Utf16SequenceLength;
            if (length > maxLength || MetadataText.IsControlOrFormat(character))
            {
                return false;
            }

            text = text[size..];
        }

        return true;
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

        url = GetMember(item, "url"u8, "url");
        path = GetMember(item, "path"u8, "path");
        return (url is null) != (path is null) && !MetadataText.HasControlOrFormatCharacter(url ?? path!);
    }

    /// <summary>
    /// Gets the links of the first 20 items of <c>links</c>, leaving out an item that is no object with exactly one of a
    /// <c>url</c> and a <c>path</c> string, or whose url or path has a control or format character; empty without an
    /// array. The members are returned without whitespace around them, a title with a control or format character as
    /// none, and the address of a link otherwise as it is held, see <see cref="MetadataLink.IsAllowedAddress"/>.
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
                var title = GetMember(item, "title"u8, "title");
                links.Add(new MetadataLink(title is not null && MetadataText.HasControlOrFormatCharacter(title) ? null : title, url, path));
            }
        }

        return links.DrainToImmutable();
    }

    // The string of a member, by its name without regard to case, trimmed as a write trims it; null when it is not set,
    // no string or blank.
    private static string? GetMember(JsonElement item, ReadOnlySpan<byte> utf8Name, string name)
    {
        foreach (var member in item.EnumerateObject())
        {
            if (member.Value.ValueKind == JsonValueKind.String && HasName(member, utf8Name, name))
            {
                var text = GetText(member.Value)?.Trim();
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }

        return null;
    }

    // Whether the member has the name, of ASCII letters, without regard to case; a name that cannot be read has none. The
    // name is read as a string only when it has escapes.
    private static bool HasName(JsonProperty member, ReadOnlySpan<byte> utf8Name, string name)
    {
        var rawName = JsonMarshal.GetRawUtf8PropertyName(member);
        if (!rawName.Contains((byte)'\\'))
        {
            return Ascii.EqualsIgnoreCase(rawName, utf8Name);
        }

        try
        {
            return string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase);
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

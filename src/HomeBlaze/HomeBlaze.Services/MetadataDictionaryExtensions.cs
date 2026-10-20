using System.Collections.Immutable;
using System.Text.Json;
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
    /// case, leaving out the items that are no string or that <see cref="MetadataText.IsAllowedTag"/> rejects.
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
            if (item.ValueKind == JsonValueKind.String && GetText(item)?.Trim() is { } tag && MetadataText.IsAllowedTag(tag) &&
                !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(tag);
            }
        }

        return tags.DrainToImmutable();
    }

    /// <summary>
    /// Gets the links of the first 20 items of the key, leaving out an item that is no object with exactly one of a
    /// <c>url</c> and a <c>path</c> string; empty without an array. The members are returned without whitespace around them,
    /// and the address of a link otherwise as it is held, see <see cref="MetadataLink.IsAllowedAddress"/>.
    /// </summary>
    public static ImmutableArray<MetadataLink> GetLinks(this IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var links = ImmutableArray.CreateBuilder<MetadataLink>();
        foreach (var item in value.EnumerateArray().Take(MetadataText.MaxItemCount))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = GetMember(item, "url");
            var path = GetMember(item, "path");
            if ((url is null) != (path is null))
            {
                links.Add(new MetadataLink(GetMember(item, "title"), url, path));
            }
        }

        return links.DrainToImmutable();
    }

    // The string of a member, by its name without regard to case, trimmed as a write trims it; null when it is not set,
    // no string or blank.
    private static string? GetMember(JsonElement item, string name)
    {
        foreach (var member in item.EnumerateObject())
        {
            if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase) && member.Value.ValueKind == JsonValueKind.String)
            {
                var text = GetText(member.Value)?.Trim();
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }

        return null;
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

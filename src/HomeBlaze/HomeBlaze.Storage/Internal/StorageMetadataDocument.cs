using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.Components.Abstractions.Pages;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The content of a metadata file. Keys this class does not know are kept, so that writing the file does not
/// remove them.
/// </summary>
internal sealed class StorageMetadataDocument
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("navTitle")]
    public string? NavigationTitle { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("location")]
    public NavigationLocation? Location { get; set; }

    [JsonPropertyName("alignment")]
    public AppBarAlignment? Alignment { get; set; }

    [JsonPropertyName("order")]
    public string?[]? Order { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>True when the document has no key at all, so that its file can be deleted.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Title is null && NavigationTitle is null && Icon is null && Location is null && Alignment is null &&
        Order is null && ExtensionData is not { Count: > 0 };

    /// <exception cref="JsonException">The content is not a JSON object.</exception>
    public static StorageMetadataDocument Parse(byte[] content)
    {
        ReadOnlySpan<byte> json = content;
        if (json.StartsWith("﻿"u8))
        {
            json = json[3..];
        }

        return JsonSerializer.Deserialize(json, StorageMetadataJsonContext.Default.StorageMetadataDocument)
            ?? new StorageMetadataDocument();
    }

    public byte[] Serialize()
        => JsonSerializer.SerializeToUtf8Bytes(this, StorageMetadataJsonContext.Default.StorageMetadataDocument);

    public StorageMetadataDocument Clone() => new()
    {
        Title = Title,
        NavigationTitle = NavigationTitle,
        Icon = Icon,
        Location = Location,
        Alignment = Alignment,
        Order = Order,
        ExtensionData = ExtensionData is null ? null : new Dictionary<string, JsonElement>(ExtensionData)
    };

    public FolderMetadata? ToFolderMetadata()
    {
        var metadata = new FolderMetadata(GetValue(Title), GetValue(NavigationTitle), GetValue(Icon));
        return metadata is { Title: null, NavigationTitle: null, Icon: null } ? null : metadata;
    }

    public SubjectMetadata? ToSubjectMetadata()
    {
        var metadata = new SubjectMetadata(GetValue(NavigationTitle), GetValue(Icon), Location, Alignment);
        return metadata is { NavigationTitle: null, Icon: null, Location: null, Alignment: null } ? null : metadata;
    }

    public ImmutableArray<string> ToChildOrder()
        => Order is null ? [] : [.. Order.OfType<string>()];

    private static string? GetValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(StorageMetadataDocument))]
internal sealed partial class StorageMetadataJsonContext : JsonSerializerContext;

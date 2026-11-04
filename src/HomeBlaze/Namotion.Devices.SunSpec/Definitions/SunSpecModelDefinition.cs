using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Devices.SunSpec.Definitions;

/// <summary>
/// A SunSpec model definition in the SunSpec Alliance JSON format.
/// </summary>
internal sealed class SunSpecModelDefinition
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("group")]
    public required SunSpecGroupDefinition Group { get; init; }
}

/// <summary>
/// A group of points: the top-level group of a model, or a nested group that may repeat.
/// </summary>
internal sealed class SunSpecGroupDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("desc")]
    public string? Description { get; init; }

    [JsonPropertyName("count")]
    [JsonConverter(typeof(SunSpecGroupCountJsonConverter))]
    public SunSpecGroupCount Count { get; init; }

    // The source-generated deserializer assigns null to init-only properties missing from the JSON,
    // overriding the initializer, so the list properties coalesce in their init accessors.
    [JsonPropertyName("points")]
    public IReadOnlyList<SunSpecPointDefinition> Points { get; init => field = value ?? []; } = [];

    [JsonPropertyName("groups")]
    public IReadOnlyList<SunSpecGroupDefinition> Groups { get; init => field = value ?? []; } = [];
}

/// <summary>
/// A point (value) of a group.
/// </summary>
internal sealed class SunSpecPointDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("size")]
    public int Size { get; init; }

    [JsonPropertyName("sf")]
    [JsonConverter(typeof(SunSpecScaleFactorJsonConverter))]
    public SunSpecScaleFactor ScaleFactor { get; init; }

    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("desc")]
    public string? Description { get; init; }

    [JsonPropertyName("access")]
    public string? Access { get; init; }

    /// <summary>
    /// Gets the fixed value the definition declares, such as the model length on the L point.
    /// </summary>
    [JsonPropertyName("value")]
    public JsonElement Value { get; init; }

    [JsonPropertyName("symbols")]
    public IReadOnlyList<SunSpecSymbolDefinition> Symbols { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Gets whether the point is writable; SunSpec points are read only unless their access is "RW".
    /// </summary>
    public bool IsWritable => Access == "RW";
}

/// <summary>
/// A named value of an enumeration point, or a named bit (by bit number) of a bit field point.
/// </summary>
internal sealed class SunSpecSymbolDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public long Value { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("desc")]
    public string? Description { get; init; }
}

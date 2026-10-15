using System.Text.Json.Serialization;

namespace HomeBlaze.History.Import.Influx;

/// <summary>The body of a Flux query request: CSV with a header row per table and no annotations.</summary>
internal sealed record FluxRequest(
    [property: JsonPropertyName("query")] string Query)
{
    [JsonPropertyName("type")]
    public string Type => "flux";

    [JsonPropertyName("dialect")]
    public FluxDialect Dialect { get; } = new();
}

internal sealed record FluxDialect
{
    [JsonPropertyName("header")]
    public bool Header => true;

    [JsonPropertyName("annotations")]
    public string[] Annotations => [];
}

[JsonSerializable(typeof(FluxRequest))]
[JsonSerializable(typeof(string))]
internal sealed partial class FluxJsonContext : JsonSerializerContext;

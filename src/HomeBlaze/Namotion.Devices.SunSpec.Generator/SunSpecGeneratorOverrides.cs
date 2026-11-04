using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Devices.SunSpec.Generator;

/// <summary>
/// Generation choices that are not part of the SunSpec definitions: excluded models, class names and model families.
/// </summary>
internal sealed class SunSpecGeneratorOverrides
{
    [JsonPropertyName("excludedModels")]
    public IReadOnlyList<int> ExcludedModels { get; init; } = [];

    [JsonPropertyName("classes")]
    public IReadOnlyList<SunSpecClassOverride> Classes { get; init; } = [];

    public static SunSpecGeneratorOverrides Load()
    {
        using var stream = typeof(SunSpecGeneratorOverrides).Assembly.GetManifestResourceStream("SunSpec/overrides.json")
            ?? throw new InvalidOperationException("The embedded overrides.json is missing.");
        return JsonSerializer.Deserialize(stream, SunSpecGeneratorJsonContext.Default.SunSpecGeneratorOverrides)
            ?? throw new InvalidDataException("overrides.json is empty.");
    }
}

/// <summary>
/// A named class for one model, or for a family of models sharing the layout of <see cref="Representative"/>.
/// </summary>
internal sealed class SunSpecClassOverride
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("models")]
    public IReadOnlyList<int> Models { get; init; } = [];

    [JsonPropertyName("representative")]
    public int? Representative { get; init; }
}

[JsonSourceGenerationOptions(AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(SunSpecGeneratorOverrides))]
internal sealed partial class SunSpecGeneratorJsonContext : JsonSerializerContext;

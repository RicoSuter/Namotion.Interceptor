using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Devices.SunSpec.Definitions;

[JsonSourceGenerationOptions(AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(SunSpecModelDefinition))]
internal sealed partial class SunSpecDefinitionJsonContext : JsonSerializerContext;

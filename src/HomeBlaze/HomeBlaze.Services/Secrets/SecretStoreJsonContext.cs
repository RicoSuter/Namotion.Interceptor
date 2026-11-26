using System.Text.Json.Serialization;

namespace HomeBlaze.Services.Secrets;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SecretStoreJsonContext : JsonSerializerContext;

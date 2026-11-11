using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Authorization;

/// <summary>Reads and writes a <see cref="PermissionAction"/> as its name.</summary>
public sealed class PermissionActionJsonConverter : JsonConverter<PermissionAction>
{
    public override PermissionAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("An action is written as its name, a string.");
        }

        var name = reader.GetString();
        return string.IsNullOrWhiteSpace(name)
            ? throw new JsonException("An action name must not be empty.")
            : new PermissionAction(name);
    }

    public override void Write(Utf8JsonWriter writer, PermissionAction value, JsonSerializerOptions options)
    {
        if (value.Name.Length == 0)
        {
            throw new JsonException("An action has no name.");
        }

        writer.WriteStringValue(value.Name);
    }
}

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Bridges an <see cref="McpToolInfo"/> to the <see cref="AIFunction"/> abstraction,
/// enabling it to be used with the ModelContextProtocol SDK and other AI function consumers.
/// </summary>
internal sealed class McpToolAIFunction : AIFunction
{
    private readonly McpToolInfo _descriptor;

    public McpToolAIFunction(McpToolInfo descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc />
    public override string Name => _descriptor.Name;

    /// <inheritdoc />
    public override string Description => _descriptor.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _descriptor.InputSchema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Build a JSON object from the arguments, preserving JsonElement values as-is
        // to avoid double-serialization (e.g., a JsonElement boolean becoming a string "true").
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var kvp in arguments)
            {
                writer.WritePropertyName(kvp.Key);
                if (kvp.Value is JsonElement jsonElement)
                {
                    jsonElement.WriteTo(writer);
                }
                else
                {
                    JsonSerializer.Serialize(writer, kvp.Value);
                }
            }
            writer.WriteEndObject();
        }

        var inputElement = JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
        return await _descriptor.Handler(inputElement, cancellationToken);
    }
}

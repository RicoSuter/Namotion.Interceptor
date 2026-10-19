using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Bridges an <see cref="McpToolInfo"/> to the <see cref="AIFunction"/> abstraction,
/// enabling it to be used with the ModelContextProtocol SDK and other AI function consumers.
/// </summary>
internal sealed class McpToolAIFunction : AIFunction
{
    private static readonly JsonTypeInfo ArgumentsTypeInfo = AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>));

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
        var inputElement = JsonSerializer.SerializeToElement(arguments, ArgumentsTypeInfo);
        return await _descriptor.Handler(inputElement, cancellationToken);
    }
}

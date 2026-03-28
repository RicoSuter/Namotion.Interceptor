using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Bridges an <see cref="McpToolDescriptor"/> to the <see cref="AIFunction"/> abstraction,
/// enabling it to be used with the ModelContextProtocol SDK and other AI function consumers.
/// </summary>
internal sealed class McpToolAIFunction : AIFunction
{
    private readonly McpToolDescriptor _descriptor;

    public McpToolAIFunction(McpToolDescriptor descriptor)
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
        // Convert the AIFunctionArguments dictionary to a JsonElement for the handler.
        var argumentsDictionary = new Dictionary<string, object?>();
        foreach (var kvp in arguments)
        {
            argumentsDictionary[kvp.Key] = kvp.Value;
        }

        var inputElement = JsonSerializer.SerializeToElement(argumentsDictionary);
        return await _descriptor.Handler(inputElement, cancellationToken);
    }
}

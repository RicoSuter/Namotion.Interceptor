using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Serves a list of <see cref="McpToolInfo"/> through the MCP list and call requests.
/// </summary>
public static class McpToolDispatcher
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static readonly JsonElement EmptyArguments = JsonElement.Parse("{}");

    /// <summary>
    /// Describes the tools for an MCP list tools request.
    /// </summary>
    public static ListToolsResult ListTools(IReadOnlyList<McpToolInfo> tools)
    {
        return new ListToolsResult
        {
            Tools = tools.Select(tool => new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.InputSchema,
                Annotations = tool.Annotations is { } annotations
                    ? new ToolAnnotations
                    {
                        ReadOnlyHint = annotations.ReadOnlyHint,
                        DestructiveHint = annotations.DestructiveHint,
                        IdempotentHint = annotations.IdempotentHint,
                        OpenWorldHint = annotations.OpenWorldHint
                    }
                    : null
            }).ToList()
        };
    }

    /// <summary>
    /// Invokes the requested tool. A string result is returned as text, any other result as indented JSON;
    /// an unknown tool or a throwing handler produces an error result instead of an exception.
    /// </summary>
    /// <exception cref="OperationCanceledException">The call was cancelled through <paramref name="cancellationToken"/>.</exception>
    public static async ValueTask<CallToolResult> CallToolAsync(
        IReadOnlyList<McpToolInfo> tools,
        CallToolRequestParams parameters,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var tool = tools.FirstOrDefault(t => t.Name == parameters.Name);
            if (tool is null)
            {
                return CreateErrorResult($"Unknown tool: {parameters.Name}");
            }

            var input = parameters.Arguments is { } arguments ? ToElement(arguments) : EmptyArguments;
            var result = await tool.Handler(input, cancellationToken);
            var text = result as string ?? JsonSerializer.Serialize(result, SerializerOptions);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text }]
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning(exception, "MCP tool '{ToolName}' failed.", parameters.Name);
            return CreateErrorResult(exception.Message);
        }
    }

    private static JsonElement ToElement(IDictionary<string, JsonElement> arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref reader);
    }

    internal static CallToolResult CreateErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        // The text is read by a model, not embedded in HTML, so non-ASCII characters and <>&' stay unescaped.
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

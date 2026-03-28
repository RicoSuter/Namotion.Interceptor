using System.Text.Json;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates the "list_types" tool for listing available types.
/// </summary>
internal class ListTypesTool
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { }
    });

    private readonly McpServerConfiguration _configuration;

    public ListTypesTool(McpServerConfiguration configuration)
    {
        _configuration = configuration;
    }

    public McpToolInfo CreateTool() => new()
    {
        Name = "list_types",
        Description = "List available types (interfaces and concrete types).",
        InputSchema = Schema,
        Handler = HandleListTypesAsync
    };

    private Task<object?> HandleListTypesAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var types = _configuration.TypeProviders
            .SelectMany(provider => provider.GetTypes())
            .ToList();

        return Task.FromResult<object?>(new { types });
    }
}

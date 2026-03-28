using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates the "get_property" tool for reading property values by path.
/// </summary>
internal class GetPropertyTool
{
    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly McpServerConfiguration _configuration;

    public GetPropertyTool(Func<IInterceptorSubject> rootSubjectProvider, McpServerConfiguration configuration)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _configuration = configuration;
    }

    public McpToolInfo CreateTool() => new()
    {
        Name = "get_property",
        Description = "Read a property value by path.",
        InputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { path = new { type = "string" } },
            required = new[] { "path" }
        }),
        Handler = HandleGetPropertyAsync
    };

    private Task<object?> HandleGetPropertyAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pathProvider = _configuration.PathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase.");

        var rootRegistered = _rootSubjectProvider().TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Root subject is not registered.");

        var path = input.GetProperty("path").GetString()!;
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, path);

        if (result is null)
        {
            return Task.FromResult<object?>(new { error = $"Path not found: {path}" });
        }

        var (property, _) = result.Value;

        var attributes = new Dictionary<string, object?>();
        foreach (var attribute in property.Attributes)
        {
            attributes[attribute.BrowseName] = attribute.GetValue();
        }

        var response = new Dictionary<string, object?>
        {
            ["value"] = property.GetValue(),
            ["type"] = property.Type.Name,
            ["isWritable"] = property.HasSetter
        };

        if (attributes.Count > 0)
        {
            response["attributes"] = attributes;
        }

        return Task.FromResult<object?>(response);
    }
}

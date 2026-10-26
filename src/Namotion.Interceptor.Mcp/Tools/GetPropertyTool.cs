using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates the "get_property" tool for reading property values by path.
/// </summary>
internal class GetPropertyTool
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { path = new { type = "string" } },
        required = new[] { "path" }
    });

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
        Description = "Read a single property value by its full path (e.g. /Devices/Sensor/Temperature). " +
                      "Returns the value, type, and any attributes. Use browse or search to discover paths first.",
        InputSchema = Schema,
        Handler = HandleGetPropertyAsync,
        Annotations = McpToolAnnotations.ReadOnly
    };

    private Task<object?> HandleGetPropertyAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pathProvider = _configuration.ToolPathProvider;

        var rootRegistered = _rootSubjectProvider().TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Root subject is not registered.");

        var path = input.GetProperty("path").GetString()!;
        var result = _configuration.TryGetReadableProperty(rootRegistered, path);

        if (result is null)
        {
            return Task.FromResult<object?>(McpToolHelper.GetPathNotFoundError(pathProvider, path));
        }

        var (property, _) = result.Value;
        if (property.CanContainSubjects)
        {
            return Task.FromResult<object?>(new
            {
                error = $"Path '{path}' points to a subject, not a scalar property. Use the 'browse' tool with this path to browse it."
            });
        }

        var attributes = new Dictionary<string, object?>();
        foreach (var attribute in property.Attributes)
        {
            if (!_configuration.IsAllowed(attribute, McpAccessActions.Read))
            {
                continue;
            }

            // A read-only attribute without a value says nothing about its property; a writable one can be set.
            var value = attribute.GetValue();
            if (value is not null || attribute.HasSetter)
            {
                attributes[attribute.BrowseName] = value;
            }
        }

        var response = new Dictionary<string, object?>
        {
            ["value"] = property.GetValue(),
            ["type"] = JsonSchemaTypeMapper.ToJsonSchemaType(property.Type)
        };

        if (McpToolHelper.IsWritable(property, _configuration))
        {
            response["isWritable"] = true;
        }

        if (attributes.Count > 0)
        {
            response["attributes"] = attributes;
        }

        return Task.FromResult<object?>(response);
    }
}

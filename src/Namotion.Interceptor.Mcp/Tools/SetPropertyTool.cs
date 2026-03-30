using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates the "set_property" tool for writing property values by path.
/// </summary>
internal class SetPropertyTool
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { path = new { type = "string" }, value = new { } },
        required = new[] { "path", "value" }
    });

    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly McpServerConfiguration _configuration;

    public SetPropertyTool(Func<IInterceptorSubject> rootSubjectProvider, McpServerConfiguration configuration)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _configuration = configuration;
    }

    public McpToolInfo CreateTool() => new()
    {
        Name = "set_property",
        Description = "Write a property value by its full path (e.g. Devices/Motor/TargetSpeed). " +
                      "Only writable properties can be set. Blocked when server is read-only.",
        InputSchema = Schema,
        Handler = HandleSetPropertyAsync
    };

    private Task<object?> HandleSetPropertyAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (_configuration.IsReadOnly)
        {
            return Task.FromResult<object?>(new { error = "Server is in read-only mode." });
        }

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
        if (property.CanContainSubjects)
        {
            return Task.FromResult<object?>(new
            {
                error = $"Path '{path}' points to a subject, not a scalar property."
            });
        }

        if (!property.HasSetter)
        {
            return Task.FromResult<object?>(new { error = $"Property is not writable: {path}" });
        }

        var valueElement = input.GetProperty("value");

        // The MCP SDK may pass values as strings (e.g., "true" instead of true).
        // If the value is a string but the target type is not, try to deserialize the string content.
        object? newValue;
        if (valueElement.ValueKind == JsonValueKind.String && property.Type != typeof(string))
        {
            var stringValue = valueElement.GetString()!;
            newValue = JsonSerializer.Deserialize(stringValue, property.Type);
        }
        else
        {
            newValue = JsonSerializer.Deserialize(valueElement.GetRawText(), property.Type);
        }

        var previousValue = property.GetValue();
        property.SetValue(newValue);

        return Task.FromResult<object?>(new { success = true, previousValue });
    }
}

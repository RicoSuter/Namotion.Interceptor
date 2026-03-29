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
        properties = new
        {
            kind = new { type = "string", description = "Filter by kind: 'interfaces', 'concrete', or 'all' (default: 'all')" },
            type = new { type = "string", description = "Search type names (case-insensitive contains match)" }
        }
    });

    private readonly McpServerConfiguration _configuration;

    public ListTypesTool(McpServerConfiguration configuration)
    {
        _configuration = configuration;
    }

    public McpToolInfo CreateTool() => new()
    {
        Name = "list_types",
        Description = "List available types. Use kind to filter interfaces/concrete. Use type to search by name. " +
                      "Interface types include property and method schemas. " +
                      "Concrete types list implemented interfaces.",
        InputSchema = Schema,
        Handler = HandleListTypesAsync
    };

    private Task<object?> HandleListTypesAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var allTypes = _configuration.TypeProviders
            .SelectMany(provider => provider.GetTypes())
            .ToList();

        // Build interfaceNames from ALL types (before filtering)
        var interfaceNames = new HashSet<string>(
            allTypes.Where(t => t.IsInterface).Select(t => t.Type.FullName!));

        var kind = input.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : "all";
        var typeSearch = input.TryGetProperty("type", out var typeSearchElement) ? typeSearchElement.GetString() : null;

        var filteredTypes = allTypes.AsEnumerable();

        if (kind == "interfaces")
        {
            filteredTypes = filteredTypes.Where(t => t.IsInterface);
        }
        else if (kind == "concrete")
        {
            filteredTypes = filteredTypes.Where(t => !t.IsInterface);
        }

        if (!string.IsNullOrEmpty(typeSearch))
        {
            filteredTypes = filteredTypes.Where(t =>
                t.Name.Contains(typeSearch, StringComparison.OrdinalIgnoreCase));
        }

        var types = filteredTypes.Select(typeInfo =>
        {
            if (typeInfo.IsInterface)
            {
                return (object)new
                {
                    name = typeInfo.Name,
                    description = typeInfo.Description,
                    isInterface = true,
                    properties = typeInfo.Type.GetProperties()
                        .Select(property => new
                        {
                            name = property.Name,
                            type = JsonSchemaTypeMapper.ToJsonSchemaType(property.PropertyType),
                            isWritable = property.CanWrite && property.GetSetMethod() != null
                        })
                        .ToArray(),
                    methods = typeInfo.Type.GetMethods()
                        .Where(method => !method.IsSpecialName && method.DeclaringType == typeInfo.Type)
                        .Select(method => new
                        {
                            name = method.Name,
                            returnType = JsonSchemaTypeMapper.ToJsonSchemaType(
                                method.ReturnType == typeof(void) ? null : method.ReturnType),
                            parameters = method.GetParameters()
                                .Select(parameter => new
                                {
                                    name = parameter.Name,
                                    type = JsonSchemaTypeMapper.ToJsonSchemaType(parameter.ParameterType)
                                })
                                .ToArray()
                        })
                        .ToArray()
                };
            }
            else
            {
                return (object)new
                {
                    name = typeInfo.Name,
                    description = typeInfo.Description,
                    isInterface = false,
                    interfaces = typeInfo.Type.GetInterfaces()
                        .Where(interfaceType => interfaceNames.Contains(interfaceType.FullName!))
                        .Select(interfaceType => interfaceType.FullName!)
                        .ToArray()
                };
            }
        }).ToList();

        return Task.FromResult<object?>(new { types });
    }
}

using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates McpToolDescriptor instances for all core and extension tools.
/// </summary>
public class McpToolFactory
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly McpServerConfiguration _configuration;

    public McpToolFactory(IInterceptorSubject rootSubject, McpServerConfiguration configuration)
    {
        _rootSubject = rootSubject;
        _configuration = configuration;
    }

    public IReadOnlyList<McpToolDescriptor> CreateTools()
    {
        var tools = new List<McpToolDescriptor>
        {
            CreateQueryTool(),
            CreateGetPropertyTool(),
            CreateSetPropertyTool(),
            CreateListTypesTool()
        };

        foreach (var provider in _configuration.ToolProviders)
        {
            tools.AddRange(provider.GetTools());
        }

        return tools;
    }

    private McpToolDescriptor CreateQueryTool()
    {
        return new McpToolDescriptor
        {
            Name = "query",
            Description = "Browse the subject tree. Paths use dot notation (e.g., root.livingRoom.temperature). " +
                          "Collections use brackets (e.g., sensors[0], devices[myDevice]).",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Starting path (default: root)" },
                    depth = new { type = "integer", description = "Max depth (default: 1)" },
                    includeProperties = new { type = "boolean", description = "Include property values (default: false)" },
                    includeAttributes = new { type = "boolean", description = "Include registry attributes on properties (default: false)" },
                    types = new { type = "array", items = new { type = "string" }, description = "Filter subjects by type/interface full names" }
                }
            }),
            Handler = HandleQueryAsync
        };
    }

    private async Task<object?> HandleQueryAsync(JsonElement input, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        var pathProvider = _configuration.PathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase for path resolution.");

        var rootRegistered = _rootSubject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Root subject is not registered.");

        var path = input.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var depth = input.TryGetProperty("depth", out var depthElement) ? depthElement.GetInt32() : 1;
        var includeProperties = input.TryGetProperty("includeProperties", out var propsElement) && propsElement.GetBoolean();
        var includeAttributes = input.TryGetProperty("includeAttributes", out var attrsElement) && attrsElement.GetBoolean();

        string[]? typeFilter = null;
        if (input.TryGetProperty("types", out var typesElement))
        {
            typeFilter = typesElement.EnumerateArray().Select(element => element.GetString()!).ToArray();
        }

        depth = Math.Min(depth, _configuration.MaxDepth);

        // Resolve starting subject
        RegisteredSubject startSubject;
        if (string.IsNullOrEmpty(path))
        {
            startSubject = rootRegistered;
        }
        else
        {
            var property = pathProvider.TryGetPropertyFromPath(rootRegistered, path);
            if (property is null)
            {
                return new { error = $"Path not found: {path}" };
            }

            var childSubject = property.GetValue() as IInterceptorSubject;
            startSubject = childSubject?.TryGetRegisteredSubject()
                ?? throw new InvalidOperationException($"Path '{path}' does not resolve to a subject.");
        }

        var subjectCount = 0;
        var truncated = false;
        var subjects = BuildSubjectTree(startSubject, pathProvider, depth, includeProperties,
            includeAttributes, typeFilter, ref subjectCount, ref truncated);

        return new
        {
            path = path ?? "",
            subjects,
            truncated,
            subjectCount
        };
    }

    private Dictionary<string, object?> BuildSubjectTree(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        int remainingDepth,
        bool includeProperties,
        bool includeAttributes,
        string[]? typeFilter,
        ref int subjectCount,
        ref bool truncated)
    {
        var result = new Dictionary<string, object?>();

        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute || !pathProvider.IsPropertyIncluded(property))
            {
                continue;
            }

            var segment = pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

            if (property.CanContainSubjects && remainingDepth > 0)
            {
                if (property.IsSubjectReference)
                {
                    var childSubject = (property.GetValue() as IInterceptorSubject)?.TryGetRegisteredSubject();
                    if (childSubject is not null && !ShouldFilterOut(childSubject, typeFilter))
                    {
                        if (subjectCount >= _configuration.MaxSubjectsPerResponse)
                        {
                            truncated = true;
                            continue;
                        }

                        subjectCount++;
                        result[segment] = BuildSubjectNode(childSubject, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes, typeFilter,
                            ref subjectCount, ref truncated);
                    }
                }
                else if (property.IsSubjectDictionary || property.IsSubjectCollection)
                {
                    var children = new Dictionary<string, object?>();
                    foreach (var child in property.Children)
                    {
                        var childRegistered = child.Subject.TryGetRegisteredSubject();
                        if (childRegistered is null || ShouldFilterOut(childRegistered, typeFilter))
                        {
                            continue;
                        }

                        if (subjectCount >= _configuration.MaxSubjectsPerResponse)
                        {
                            truncated = true;
                            break;
                        }

                        subjectCount++;
                        var key = child.Index?.ToString() ?? child.Subject.GetHashCode().ToString();
                        children[key] = BuildSubjectNode(childRegistered, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes, typeFilter,
                            ref subjectCount, ref truncated);
                    }

                    if (children.Count > 0)
                    {
                        result[segment] = children;
                    }
                }
            }
            else if (includeProperties && !property.CanContainSubjects)
            {
                result[segment] = BuildPropertyValue(property, includeAttributes);
            }
        }

        return result;
    }

    private Dictionary<string, object?> BuildSubjectNode(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        int remainingDepth,
        bool includeProperties,
        bool includeAttributes,
        string[]? typeFilter,
        ref int subjectCount,
        ref bool truncated)
    {
        var node = new Dictionary<string, object?>();

        // Subject-level metadata from enrichers
        foreach (var enricher in _configuration.SubjectEnrichers)
        {
            enricher.EnrichSubject(subject, node);
        }

        // $hasChildren
        var hasChildren = subject.Properties.Any(property =>
            !property.IsAttribute && pathProvider.IsPropertyIncluded(property) && property.CanContainSubjects);
        node["$hasChildren"] = hasChildren;

        // Properties and child subjects
        var tree = BuildSubjectTree(subject, pathProvider, remainingDepth,
            includeProperties, includeAttributes, typeFilter, ref subjectCount, ref truncated);

        foreach (var kvp in tree)
        {
            node[kvp.Key] = kvp.Value;
        }

        return node;
    }

    private static object? BuildPropertyValue(RegisteredSubjectProperty property, bool includeAttributes)
    {
        if (!includeAttributes)
        {
            return new { value = property.GetValue() };
        }

        var attributes = new Dictionary<string, object?>();
        foreach (var attribute in property.Attributes)
        {
            attributes[attribute.BrowseName] = attribute.GetValue();
        }

        var result = new Dictionary<string, object?>
        {
            ["value"] = property.GetValue()
        };

        if (attributes.Count > 0)
        {
            result["attributes"] = attributes;
        }

        return result;
    }

    private static bool ShouldFilterOut(RegisteredSubject subject, string[]? typeFilter)
    {
        if (typeFilter is null || typeFilter.Length == 0)
        {
            return false;
        }

        var subjectType = subject.Subject.GetType();
        foreach (var filter in typeFilter)
        {
            if (subjectType.FullName == filter || subjectType.Name == filter)
            {
                return false;
            }

            if (subjectType.GetInterfaces().Any(interfaceType => interfaceType.FullName == filter || interfaceType.Name == filter))
            {
                return false;
            }
        }

        return true;
    }

    // Stub implementations for remaining tools - implemented in subsequent tasks
    private McpToolDescriptor CreateGetPropertyTool() => new()
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

    private McpToolDescriptor CreateSetPropertyTool() => new()
    {
        Name = "set_property",
        Description = "Write a property value by path.",
        InputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { path = new { type = "string" }, value = new { } },
            required = new[] { "path", "value" }
        }),
        Handler = HandleSetPropertyAsync
    };

    private McpToolDescriptor CreateListTypesTool() => new()
    {
        Name = "list_types",
        Description = "List available types (interfaces and concrete types).",
        InputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        }),
        Handler = HandleListTypesAsync
    };

    private Task<object?> HandleGetPropertyAsync(JsonElement input, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    private Task<object?> HandleSetPropertyAsync(JsonElement input, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    private Task<object?> HandleListTypesAsync(JsonElement input, CancellationToken cancellationToken)
        => throw new NotImplementedException();
}

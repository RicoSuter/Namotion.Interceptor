using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates the "browse" tool for browsing the subject tree.
/// </summary>
internal class BrowseTool
{
    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly McpServerConfiguration _configuration;

    public BrowseTool(Func<IInterceptorSubject> rootSubjectProvider, McpServerConfiguration configuration)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _configuration = configuration;
    }

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Starting path (default: root)" },
            depth = new { type = "integer", description = "Max depth (default: 1)" },
            includeProperties = new { type = "boolean", description = "Include property values (default: false)" },
            includeAttributes = new { type = "boolean", description = "Include registry attributes on properties (default: false)" },
            includeMethods = new { type = "boolean", description = "Include $methods in subject nodes (default: false)" },
            includeInterfaces = new { type = "boolean", description = "Include $interfaces in subject nodes (default: false)" },
            maxSubjects = new { type = "integer", description = "Maximum subjects to return (default: server limit)" },
            excludeTypes = new { type = "array", items = new { type = "string" }, description = "Exclude subjects matching these type/interface names" }
        }
    });

    public McpToolInfo CreateTool()
    {
        return new McpToolInfo
        {
            Name = "browse",
            Description = "Browse the subject tree at a path with configurable depth. " +
                          "Use depth=0 with includeProperties=true to see all properties of a subject. " +
                          "Paths: '/' separators, brackets for indices (Pins[0]). " +
                          "To find subjects by type, use search with types instead.",
            InputSchema = Schema,
            Handler = HandleBrowseAsync
        };
    }

    private Task<object?> HandleBrowseAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pathProvider = _configuration.PathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase for path resolution.");

        var rootRegistered = _rootSubjectProvider().TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Root subject is not registered.");

        var path = input.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var depth = input.TryGetProperty("depth", out var depthElement) ? depthElement.GetInt32() : 1;
        var includeProperties = input.TryGetProperty("includeProperties", out var propsElement) && propsElement.GetBoolean();
        var includeAttributes = input.TryGetProperty("includeAttributes", out var attrsElement) && attrsElement.GetBoolean();
        var includeMethods = input.TryGetProperty("includeMethods", out var methodsElement) && methodsElement.GetBoolean();
        var includeInterfaces = input.TryGetProperty("includeInterfaces", out var interfacesElement) && interfacesElement.GetBoolean();
        var maxSubjects = input.TryGetProperty("maxSubjects", out var maxElement)
            ? Math.Min(maxElement.GetInt32(), _configuration.MaxSubjectsPerResponse)
            : _configuration.MaxSubjectsPerResponse;

        string[]? excludeTypes = null;
        if (input.TryGetProperty("excludeTypes", out var excludeElement))
        {
            excludeTypes = excludeElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }

        depth = Math.Min(depth, _configuration.MaxDepth);

        // Resolve starting subject
        RegisteredSubject startSubject;
        var isRootPath = string.IsNullOrEmpty(path) ||
            (_configuration.PathPrefix.Length > 0 && path == _configuration.PathPrefix);

        if (isRootPath)
        {
            startSubject = rootRegistered;
        }
        else
        {
            var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, path!);
            if (resolved is null)
            {
                return Task.FromResult<object?>(new { error = $"Path not found: {path}" });
            }

            startSubject = resolved;
        }

        var subjectCount = 0;
        var truncated = false;
        var visited = new HashSet<IInterceptorSubject>();
        var result = BuildSubjectNode(startSubject, pathProvider, depth, includeProperties,
            includeAttributes, includeMethods, includeInterfaces, excludeTypes, visited, maxSubjects, ref subjectCount, ref truncated);

        return Task.FromResult<object?>(new
        {
            result,
            truncated,
            subjectCount
        });
    }

    private Dictionary<string, object?> BuildSubjectTree(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        int remainingDepth,
        bool includeProperties,
        bool includeAttributes,
        bool includeMethods,
        bool includeInterfaces,
        string[]? excludeTypes,
        HashSet<IInterceptorSubject> visited,
        int maxSubjects,
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

            if (property.CanContainSubjects)
            {
                if (remainingDepth > 0)
                {
                    if (property.IsSubjectReference)
                    {
                        var child = property.Children.FirstOrDefault();
                        var childSubject = child.Subject?.TryGetRegisteredSubject();
                        if (child.Subject is not null &&
                            childSubject is not null &&
                            !McpToolHelper.ShouldExcludeByType(childSubject, excludeTypes) &&
                            visited.Add(child.Subject))
                        {
                            if (subjectCount >= maxSubjects)
                            {
                                truncated = true;
                                continue;
                            }

                            subjectCount++;
                            result[segment] = BuildSubjectNode(childSubject, pathProvider,
                                remainingDepth - 1, includeProperties, includeAttributes,
                                includeMethods, includeInterfaces, excludeTypes,
                                visited, maxSubjects, ref subjectCount, ref truncated);
                        }
                    }
                    else if (property.IsSubjectDictionary || property.IsSubjectCollection)
                    {
                        var target = new Dictionary<string, object?>();

                        foreach (var child in property.Children)
                        {
                            var childRegistered = child.Subject.TryGetRegisteredSubject();
                            if (childRegistered is null ||
                                McpToolHelper.ShouldExcludeByType(childRegistered, excludeTypes) ||
                                !visited.Add(child.Subject))
                            {
                                continue;
                            }

                            if (subjectCount >= maxSubjects)
                            {
                                truncated = true;
                                break;
                            }

                            subjectCount++;
                            var key = child.Index?.ToString() ?? child.Subject.GetHashCode().ToString();
                            target[key] = BuildSubjectNode(childRegistered, pathProvider,
                                remainingDepth - 1, includeProperties, includeAttributes,
                                includeMethods, includeInterfaces, excludeTypes,
                                visited, maxSubjects, ref subjectCount, ref truncated);
                        }

                        if (target.Count > 0)
                        {
                            result[segment] = target;
                        }
                    }
                }
                else if (property.Children.Length > 0)
                {
                    var summary = new Dictionary<string, object?>
                    {
                        ["$count"] = property.Children.Length
                    };

                    // Auto-detect child type if all children share the same type
                    var firstType = property.Children[0].Subject.GetType();
                    if (property.Children.All(child => child.Subject.GetType() == firstType))
                    {
                        summary["$itemType"] = firstType.Name;
                    }

                    result[segment] = summary;
                }
            }
            else if (includeProperties)
            {
                result[segment] = McpToolHelper.BuildPropertyValue(property, includeAttributes, _configuration.IsReadOnly);
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
        bool includeMethods,
        bool includeInterfaces,
        string[]? excludeTypes,
        HashSet<IInterceptorSubject> visited,
        int maxSubjects,
        ref int subjectCount,
        ref bool truncated)
    {
        var node = McpToolHelper.BuildSubjectNode(
            subject, pathProvider, _rootSubjectProvider(), _configuration,
            includeProperties: false, includeAttributes: false);

        McpToolHelper.FilterEnrichments(node, includeMethods, includeInterfaces);

        // Properties and child subjects (handled by tree traversal, not flat helper)
        var tree = BuildSubjectTree(subject, pathProvider, remainingDepth,
            includeProperties, includeAttributes, includeMethods, includeInterfaces, excludeTypes,
            visited, maxSubjects, ref subjectCount, ref truncated);

        foreach (var kvp in tree)
        {
            node[kvp.Key] = kvp.Value;
        }

        return node;
    }
}

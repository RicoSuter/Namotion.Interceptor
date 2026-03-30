using System.Text.Json;
using Namotion.Interceptor.Mcp.Models;
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
            format = new { type = "string", @enum = new[] { "text", "json" }, description = "Output format: 'text' (default) for LLM-readable overview, 'json' for exact structured data" },
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
                          "Use depth=0 with includeProperties=true to inspect a single subject's properties. " +
                          "Paths use '/' separators and brackets for indices (e.g. Devices/Pins[0]). " +
                          "To find subjects by capability, use list_types then search with types instead. " +
                          "Use format=json for structured data, text (default) for overview.",
            InputSchema = Schema,
            Handler = HandleBrowseAsync
        };
    }

    private Task<object?> HandleBrowseAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pathProvider = _configuration.PathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase for path resolution.");

        var rootSubject = _rootSubjectProvider();
        var rootRegisteredSubject = rootSubject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Root subject is not registered.");

        var format = input.TryGetProperty("format", out var formatElement) ? formatElement.GetString() : "text";
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
            startSubject = rootRegisteredSubject;
        }
        else
        {
            var resolved = pathProvider.TryGetSubjectFromPath(rootRegisteredSubject, path!);
            if (resolved is null)
            {
                return Task.FromResult<object?>(new { error = $"Path not found: {path}" });
            }

            startSubject = resolved;
        }

        var subjectCount = 0;
        var truncated = false;
        var visited = new HashSet<IInterceptorSubject>();
        var result = BuildSubjectNode(startSubject, rootSubject, pathProvider, depth, includeProperties,
            includeAttributes, includeMethods, includeInterfaces, excludeTypes, visited, maxSubjects, ref subjectCount, ref truncated);

        var browseResult = new BrowseResult { Result = result, SubjectCount = subjectCount, Truncated = truncated };

        if (format == "json")
        {
            return Task.FromResult<object?>(browseResult);
        }

        return Task.FromResult<object?>(McpTextFormatter.FormatBrowseResult(browseResult));
    }

    private SubjectNode BuildSubjectNode(
        RegisteredSubject subject,
        IInterceptorSubject rootSubject,
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
        var node = McpToolHelper.BuildSubjectNodeDto(
            subject, pathProvider, rootSubject, _configuration,
            includeProperties, includeAttributes, includeMethods, includeInterfaces);

        // Add subject-containing properties via tree traversal
        BuildSubjectTree(node, subject, rootSubject, pathProvider, remainingDepth,
            includeProperties, includeAttributes, includeMethods, includeInterfaces, excludeTypes,
            visited, maxSubjects, ref subjectCount, ref truncated);

        return node;
    }

    private void BuildSubjectTree(
        SubjectNode node,
        RegisteredSubject subject,
        IInterceptorSubject rootSubject,
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
        node.Properties ??= new Dictionary<string, SubjectNodeProperty>();

        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute || !pathProvider.IsPropertyIncluded(property))
            {
                continue;
            }

            if (!property.CanContainSubjects)
            {
                continue; // Scalar properties already handled by BuildSubjectNodeDto
            }

            var segment = pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

            if (remainingDepth > 0)
            {
                if (property.IsSubjectReference)
                {
                    var child = property.Children.FirstOrDefault();
                    var childSubject = child.Subject?.TryGetRegisteredSubject();
                    if (child.Subject is not null &&
                        childSubject is not null &&
                        !McpToolHelper.ShouldExcludeByType(childSubject, _configuration.ExcludeTypes, excludeTypes) &&
                        visited.Add(child.Subject))
                    {
                        if (subjectCount >= maxSubjects)
                        {
                            truncated = true;
                            continue;
                        }

                        subjectCount++;
                        var childNode = BuildSubjectNode(childSubject, rootSubject, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes,
                            includeMethods, includeInterfaces, excludeTypes,
                            visited, maxSubjects, ref subjectCount, ref truncated);
                        node.Properties[segment] = new SubjectObjectProperty(childNode);
                    }
                }
                else if (property.IsSubjectDictionary)
                {
                    var children = new Dictionary<string, SubjectNode>();

                    foreach (var child in property.Children)
                    {
                        var childRegistered = child.Subject.TryGetRegisteredSubject();
                        if (childRegistered is null ||
                            McpToolHelper.ShouldExcludeByType(childRegistered, _configuration.ExcludeTypes, excludeTypes) ||
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
                        children[key] = BuildSubjectNode(childRegistered, rootSubject, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes,
                            includeMethods, includeInterfaces, excludeTypes,
                            visited, maxSubjects, ref subjectCount, ref truncated);
                    }

                    if (children.Count > 0)
                    {
                        node.Properties[segment] = new SubjectDictionaryProperty(Children: children);
                    }
                }
                else if (property.IsSubjectCollection)
                {
                    var children = new List<SubjectNode>();

                    foreach (var child in property.Children)
                    {
                        var childRegistered = child.Subject.TryGetRegisteredSubject();
                        if (childRegistered is null ||
                            McpToolHelper.ShouldExcludeByType(childRegistered, _configuration.ExcludeTypes, excludeTypes) ||
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
                        children.Add(BuildSubjectNode(childRegistered, rootSubject, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes,
                            includeMethods, includeInterfaces, excludeTypes,
                            visited, maxSubjects, ref subjectCount, ref truncated));
                    }

                    if (children.Count > 0)
                    {
                        node.Properties[segment] = new SubjectCollectionProperty(Children: children);
                    }
                }
            }
            else if (property.Children.Length > 0)
            {
                // Collapsed at depth boundary
                var count = property.Children.Length;
                string? itemType = null;
                var firstType = property.Children[0].Subject.GetType();
                if (property.Children.All(child => child.Subject.GetType() == firstType))
                {
                    itemType = firstType.Name;
                }

                if (property.IsSubjectReference)
                {
                    node.Properties[segment] = new SubjectObjectProperty(Child: null, IsCollapsed: true);
                }
                else if (property.IsSubjectDictionary)
                {
                    node.Properties[segment] = new SubjectDictionaryProperty(IsCollapsed: true, Count: count, ItemType: itemType);
                }
                else
                {
                    node.Properties[segment] = new SubjectCollectionProperty(IsCollapsed: true, Count: count, ItemType: itemType);
                }
            }
        }
    }
}

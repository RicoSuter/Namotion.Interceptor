using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates the "query" tool for browsing the subject tree.
/// </summary>
internal class QueryTool
{
    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly McpServerConfiguration _configuration;

    public QueryTool(Func<IInterceptorSubject> rootSubjectProvider, McpServerConfiguration configuration)
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
            types = new { type = "array", items = new { type = "string" }, description = "Filter subjects by type/interface full names" }
        }
    });

    public McpToolInfo CreateTool()
    {
        return new McpToolInfo
        {
            Name = "query",
            Description = "Browse the subject tree. Paths use separator notation (e.g., Folder/SubFolder/Device). " +
                          "Collections use brackets (e.g., Pins[0], Items[myKey]). " +
                          "Subjects in the response include $path for use with get_property/set_property. " +
                          "At depth 0, properties with children show $count instead of expanding.",
            InputSchema = Schema,
            Handler = HandleQueryAsync
        };
    }

    private async Task<object?> HandleQueryAsync(JsonElement input, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        var pathProvider = _configuration.PathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase for path resolution.");

        var rootRegistered = _rootSubjectProvider().TryGetRegisteredSubject()
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

        // When a type filter is provided, do a flat search over KnownSubjects
        if (typeFilter is not null && typeFilter.Length > 0)
        {
            return HandleFilteredQuery(pathProvider, typeFilter,
                includeProperties, includeAttributes);
        }

        // Resolve starting subject
        RegisteredSubject startSubject;
        if (string.IsNullOrEmpty(path))
        {
            startSubject = rootRegistered;
        }
        else
        {
            var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, path);
            if (resolved is null)
            {
                return new { error = $"Path not found: {path}" };
            }

            startSubject = resolved;
        }

        var subjectCount = 0;
        var truncated = false;
        var visited = new HashSet<IInterceptorSubject>();
        var subjects = BuildSubjectTree(startSubject, pathProvider, depth, includeProperties,
            includeAttributes, null, visited, ref subjectCount, ref truncated);

        return new
        {
            path = path ?? "",
            subjects,
            truncated,
            subjectCount
        };
    }

    private object HandleFilteredQuery(
        PathProviderBase pathProvider,
        string[] typeFilter,
        bool includeProperties,
        bool includeAttributes)
    {
        var registry = _rootSubjectProvider().Context.TryGetService<ISubjectRegistry>();
        if (registry is null)
        {
            return new { error = "Subject registry is not available." };
        }

        var subjects = new Dictionary<string, object?>();
        var truncated = false;
        var rootSubject = _rootSubjectProvider();

        foreach (var (_, registered) in registry.KnownSubjects)
        {
            if (ShouldFilterOut(registered, typeFilter))
            {
                continue;
            }

            if (subjects.Count >= _configuration.MaxSubjectsPerResponse)
            {
                truncated = true;
                break;
            }

            // Compute subject path via its parent property + index
            string? subjectPath;
            if (registered.Subject == rootSubject)
            {
                subjectPath = "";
            }
            else if (registered.Parents.Length > 0)
            {
                var parent = registered.Parents[0];
                subjectPath = parent.Property.TryGetPath(pathProvider, rootSubject, parent.Index);
            }
            else
            {
                continue;
            }

            if (subjectPath is null)
            {
                continue;
            }

            var node = new Dictionary<string, object?>();

            // Enrichers
            foreach (var enricher in _configuration.SubjectEnrichers)
            {
                foreach (var kvp in enricher.GetSubjectEnrichments(registered))
                {
                    node[kvp.Key] = kvp.Value;
                }
            }

            // Properties
            if (includeProperties)
            {
                foreach (var property in registered.Properties)
                {
                    if (property.IsAttribute || !pathProvider.IsPropertyIncluded(property) || property.CanContainSubjects)
                    {
                        continue;
                    }

                    var segment = pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;
                    node[segment] = McpToolHelper.BuildPropertyValue(property, includeAttributes);
                }
            }

            subjects[subjectPath] = node;
        }

        return new
        {
            subjects,
            truncated,
            subjectCount = subjects.Count
        };
    }

    private Dictionary<string, object?> BuildSubjectTree(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        int remainingDepth,
        bool includeProperties,
        bool includeAttributes,
        string[]? typeFilter,
        HashSet<IInterceptorSubject> visited,
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
                            !ShouldFilterOut(childSubject, typeFilter) &&
                            visited.Add(child.Subject))
                        {
                            if (subjectCount >= _configuration.MaxSubjectsPerResponse)
                            {
                                truncated = true;
                                continue;
                            }

                            subjectCount++;
                            result[segment] = BuildSubjectNode(childSubject, pathProvider,
                                remainingDepth - 1, includeProperties, includeAttributes, typeFilter,
                                visited, ref subjectCount, ref truncated);
                        }
                    }
                    else if (property.IsSubjectDictionary || property.IsSubjectCollection)
                    {
                        var target = new Dictionary<string, object?>();

                        foreach (var child in property.Children)
                        {
                            var childRegistered = child.Subject.TryGetRegisteredSubject();
                            if (childRegistered is null ||
                                ShouldFilterOut(childRegistered, typeFilter) ||
                                !visited.Add(child.Subject))
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
                            target[key] = BuildSubjectNode(childRegistered, pathProvider,
                                remainingDepth - 1, includeProperties, includeAttributes, typeFilter,
                                visited, ref subjectCount, ref truncated);
                        }

                        if (target.Count > 0)
                        {
                            result[segment] = target;
                        }
                    }
                }
                else if (property.Children.Length > 0)
                {
                    result[segment] = new Dictionary<string, object?>
                    {
                        ["$count"] = property.Children.Length
                    };
                }
            }
            else if (includeProperties)
            {
                result[segment] = McpToolHelper.BuildPropertyValue(property, includeAttributes);
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
        HashSet<IInterceptorSubject> visited,
        ref int subjectCount,
        ref bool truncated)
    {
        var node = new Dictionary<string, object?>();
        var rootSubject = _rootSubjectProvider();

        // $path — compute the path to this subject for use with get_property/set_property
        if (subject.Subject != rootSubject && subject.Parents.Length > 0)
        {
            var parent = subject.Parents[0];
            var subjectPath = parent.Property.TryGetPath(pathProvider, rootSubject, parent.Index);
            if (subjectPath is not null)
            {
                node["$path"] = subjectPath;
            }
        }

        // Subject-level metadata from enrichers
        foreach (var enricher in _configuration.SubjectEnrichers)
        {
            foreach (var kvp in enricher.GetSubjectEnrichments(subject))
            {
                node[kvp.Key] = kvp.Value;
            }
        }

        // Properties and child subjects
        var tree = BuildSubjectTree(subject, pathProvider, remainingDepth,
            includeProperties, includeAttributes, typeFilter, visited, ref subjectCount, ref truncated);

        foreach (var kvp in tree)
        {
            node[kvp.Key] = kvp.Value;
        }

        return node;
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
}

using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
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

    public McpToolInfo CreateTool()
    {
        return new McpToolInfo
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
            return HandleFilteredQuery(rootRegistered, pathProvider, typeFilter,
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
        var subjects = BuildSubjectTree(startSubject, pathProvider, depth, includeProperties,
            includeAttributes, null, ref subjectCount, ref truncated);

        return new
        {
            path = path ?? "",
            subjects,
            truncated,
            subjectCount
        };
    }

    private object HandleFilteredQuery(
        RegisteredSubject rootRegistered,
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

        foreach (var (subject, registered) in registry.KnownSubjects)
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

            // Compute subject path from any included property
            var anyProperty = registered.Properties.FirstOrDefault(p =>
                !p.IsAttribute && pathProvider.IsPropertyIncluded(p));

            var propertyPath = anyProperty?.TryGetPath(pathProvider, rootSubject);
            if (propertyPath is null)
            {
                continue;
            }

            // Strip the last segment (property name) to get the subject path
            var lastDot = propertyPath.LastIndexOf(pathProvider.PathSeparator);
            var subjectPath = lastDot >= 0 ? propertyPath[..lastDot] : "";

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

    internal Dictionary<string, object?> BuildSubjectTree(
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
                    // [InlinePaths]: children are inlined directly into the parent result
                    var isInline = InlinePathsAttribute.IsInlinePathsProperty(
                        subject.Subject.GetType(), property.Name);
                    var target = isInline ? result : new Dictionary<string, object?>();

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
                        target[key] = BuildSubjectNode(childRegistered, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes, typeFilter,
                            ref subjectCount, ref truncated);
                    }

                    if (!isInline && target.Count > 0)
                    {
                        result[segment] = target;
                    }
                }
            }
            else if (includeProperties && !property.CanContainSubjects)
            {
                result[segment] = McpToolHelper.BuildPropertyValue(property, includeAttributes);
            }
        }

        return result;
    }

    internal Dictionary<string, object?> BuildSubjectNode(
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
            foreach (var kvp in enricher.GetSubjectEnrichments(subject))
            {
                node[kvp.Key] = kvp.Value;
            }
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

    internal static bool ShouldFilterOut(RegisteredSubject subject, string[]? typeFilter)
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

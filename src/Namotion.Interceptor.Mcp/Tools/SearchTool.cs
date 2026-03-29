using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates the "search" tool for flat search across all known subjects.
/// </summary>
internal class SearchTool
{
    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly McpServerConfiguration _configuration;

    public SearchTool(Func<IInterceptorSubject> rootSubjectProvider, McpServerConfiguration configuration)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _configuration = configuration;
    }

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            text = new { type = "string", description = "Search text (matches against title and path, case-insensitive)" },
            types = new { type = "array", items = new { type = "string" }, description = "Filter by type/interface names" },
            includeProperties = new { type = "boolean", description = "Include property values (default: false)" },
            includeAttributes = new { type = "boolean", description = "Include registry attributes on properties (default: false)" },
            includeMethods = new { type = "boolean", description = "Include $methods in subject nodes (default: false)" },
            includeInterfaces = new { type = "boolean", description = "Include $interfaces in subject nodes (default: false)" },
            maxSubjects = new { type = "integer", description = "Maximum subjects to return (default: server limit)" },
            excludeTypes = new { type = "array", items = new { type = "string" }, description = "Exclude subjects matching these type/interface names" },
            path = new { type = "string", description = "Scope search to a subtree path prefix" }
        }
    });

    public McpToolInfo CreateTool() => new()
    {
        Name = "search",
        Description = "Search across all subjects. Filter by text (matches title and path) and/or type names. " +
                      "Use path to scope search to a subtree. " +
                      "Use includeMethods/includeInterfaces to see capabilities. " +
                      "Use excludeTypes to hide noisy types. " +
                      "Returns a flat list of matching subjects with $path for use with get_property/set_property.",
        InputSchema = Schema,
        Handler = HandleSearchAsync
    };

    private Task<object?> HandleSearchAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pathProvider = _configuration.PathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase.");

        var registry = _rootSubjectProvider().Context.TryGetService<ISubjectRegistry>();
        if (registry is null)
        {
            return Task.FromResult<object?>(new { error = "Subject registry is not available." });
        }

        var text = input.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        var includeProperties = input.TryGetProperty("includeProperties", out var propsElement) && propsElement.GetBoolean();
        var includeAttributes = input.TryGetProperty("includeAttributes", out var attrsElement) && attrsElement.GetBoolean();
        var includeMethods = input.TryGetProperty("includeMethods", out var methodsElement) && methodsElement.GetBoolean();
        var includeInterfaces = input.TryGetProperty("includeInterfaces", out var interfacesElement) && interfacesElement.GetBoolean();

        var maxSubjects = input.TryGetProperty("maxSubjects", out var maxElement)
            ? Math.Min(maxElement.GetInt32(), _configuration.MaxSubjectsPerResponse)
            : _configuration.MaxSubjectsPerResponse;

        string[]? typeFilter = null;
        if (input.TryGetProperty("types", out var typesElement))
        {
            typeFilter = typesElement.EnumerateArray().Select(element => element.GetString()!).ToArray();
        }

        string[]? excludeTypes = null;
        if (input.TryGetProperty("excludeTypes", out var excludeElement))
        {
            excludeTypes = excludeElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }

        var pathPrefix = input.TryGetProperty("path", out var pathPrefixElement) ? pathPrefixElement.GetString() : null;

        var subjects = new Dictionary<string, object?>();
        var truncated = false;
        var rootSubject = _rootSubjectProvider();

        foreach (var (_, registered) in registry.KnownSubjects)
        {
            if (typeFilter is not null && typeFilter.Length > 0 && ShouldFilterOutByType(registered, typeFilter))
            {
                continue;
            }

            if (McpToolHelper.ShouldExcludeByType(registered, excludeTypes))
            {
                continue;
            }

            var subjectPath = McpToolHelper.TryGetSubjectPath(registered, pathProvider, rootSubject);
            if (subjectPath is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(pathPrefix) &&
                !subjectPath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Compute enrichments early (needed for text matching on $title)
            var node = new Dictionary<string, object?> { ["$path"] = subjectPath };
            McpToolHelper.ApplyEnrichments(node, registered, _configuration);

            // Text filter — match against path and $title
            if (!string.IsNullOrEmpty(text))
            {
                var matchesPath = subjectPath.Contains(text, StringComparison.OrdinalIgnoreCase);
                var matchesTitle = node.TryGetValue("$title", out var title) &&
                                   title is string titleString &&
                                   titleString.Contains(text, StringComparison.OrdinalIgnoreCase);

                if (!matchesPath && !matchesTitle)
                {
                    continue;
                }
            }

            if (subjects.Count >= maxSubjects)
            {
                truncated = true;
                break;
            }

            McpToolHelper.FilterEnrichments(node, includeMethods, includeInterfaces);

            McpToolHelper.ApplyProperties(node, registered, pathProvider,
                includeProperties, includeAttributes, _configuration.IsReadOnly);

            subjects[subjectPath] = node;
        }

        return Task.FromResult<object?>(new
        {
            results = subjects,
            truncated,
            subjectCount = subjects.Count
        });
    }

    private static bool ShouldFilterOutByType(RegisteredSubject subject, string[] typeFilter)
    {
        var subjectType = subject.Subject.GetType();
        foreach (var filter in typeFilter)
        {
            if (subjectType.FullName == filter || subjectType.Name == filter)
            {
                return false;
            }

            if (subjectType.GetInterfaces().Any(interfaceType =>
                interfaceType.FullName == filter || interfaceType.Name == filter))
            {
                return false;
            }
        }

        return true;
    }
}

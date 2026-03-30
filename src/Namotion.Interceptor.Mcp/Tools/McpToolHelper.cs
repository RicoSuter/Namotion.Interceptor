using Namotion.Interceptor.Mcp.Models;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Shared helper methods for MCP tool implementations.
/// </summary>
internal static class McpToolHelper
{
    internal static string? TryGetSubjectPath(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        IInterceptorSubject rootSubject,
        string pathPrefix = "")
    {
        if (subject.Subject == rootSubject)
        {
            return pathPrefix.Length > 0 ? pathPrefix : null;
        }

        if (subject.Parents.Length > 0)
        {
            var parent = subject.Parents[0];
            var path = parent.Property.TryGetPath(pathProvider, rootSubject, parent.Index);
            return path is not null ? $"{pathPrefix}{path}" : null;
        }

        return null;
    }

    internal static bool ShouldExcludeByType(RegisteredSubject subject, string[]? excludeTypes)
    {
        if (excludeTypes is null || excludeTypes.Length == 0)
        {
            return false;
        }

        var subjectType = subject.Subject.GetType();
        foreach (var exclude in excludeTypes)
        {
            if (subjectType.FullName == exclude || subjectType.Name == exclude)
            {
                return true;
            }

            if (subjectType.GetInterfaces().Any(i => i.FullName == exclude || i.Name == exclude))
            {
                return true;
            }
        }

        return false;
    }

    internal static SubjectNode BuildSubjectNodeDto(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        IInterceptorSubject rootSubject,
        McpServerConfiguration configuration,
        bool includeProperties,
        bool includeAttributes,
        bool includeMethods,
        bool includeInterfaces)
    {
        var path = TryGetSubjectPath(subject, pathProvider, rootSubject, configuration.PathPrefix);

        // Collect enrichments and extract known fields
        var enrichments = new Dictionary<string, object?>();
        foreach (var enricher in configuration.SubjectEnrichers)
        {
            foreach (var kvp in enricher.GetSubjectEnrichments(subject))
            {
                enrichments[kvp.Key] = kvp.Value;
            }
        }

        // Extract known metadata from enrichments
        var type = subject.Subject.GetType().FullName;
        if (enrichments.Remove("$type", out var typeOverride) && typeOverride is string typeStr)
        {
            type = typeStr;
        }

        string? title = null;
        if (enrichments.Remove("$title", out var titleObj) && titleObj is string titleStr)
        {
            title = titleStr;
        }

        string[]? methods = null;
        if (includeMethods && enrichments.Remove("$methods", out var methodsObj))
        {
            methods = ToStringArray(methodsObj);
        }
        else
        {
            enrichments.Remove("$methods");
        }

        string[]? interfaces = null;
        if (includeInterfaces && enrichments.Remove("$interfaces", out var interfacesObj))
        {
            interfaces = ToStringArray(interfacesObj);
        }
        else
        {
            enrichments.Remove("$interfaces");
        }

        // Build scalar properties
        List<SubjectNodeProperty>? properties = null;
        if (includeProperties)
        {
            properties = [];
            foreach (var property in subject.Properties)
            {
                if (property.IsAttribute || !pathProvider.IsPropertyIncluded(property) || property.CanContainSubjects)
                {
                    continue;
                }

                var segment = pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;
                properties.Add(BuildScalarPropertyDto(property, segment, includeAttributes, configuration.IsReadOnly));
            }
        }

        return new SubjectNode
        {
            Path = path,
            Type = type,
            Title = title,
            Enrichments = enrichments.Count > 0 ? enrichments : null,
            Methods = methods,
            Interfaces = interfaces,
            Properties = properties
        };
    }

    internal static ScalarProperty BuildScalarPropertyDto(
        RegisteredSubjectProperty property, string name, bool includeAttributes, bool isReadOnly)
    {
        List<PropertyAttribute>? attributes = null;
        if (includeAttributes)
        {
            attributes = [];
            foreach (var attribute in property.Attributes)
            {
                attributes.Add(new PropertyAttribute(attribute.BrowseName, attribute.GetValue()));
            }

            if (attributes.Count == 0)
            {
                attributes = null;
            }
        }

        return new ScalarProperty(
            name,
            property.GetValue(),
            JsonSchemaTypeMapper.ToJsonSchemaType(property.Type) ?? "object",
            IsWritable: !isReadOnly && property.HasSetter,
            Attributes: attributes);
    }

    private static string[]? ToStringArray(object? value)
    {
        if (value is string[] array) return array;
        if (value is IEnumerable<object?> enumerable)
            return enumerable.Select(item => item?.ToString() ?? "").ToArray();
        return null;
    }
}

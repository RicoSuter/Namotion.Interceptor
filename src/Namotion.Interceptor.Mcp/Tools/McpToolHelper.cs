using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Shared helper methods for MCP tool implementations.
/// </summary>
internal static class McpToolHelper
{
    internal static object? BuildPropertyValue(
        RegisteredSubjectProperty property,
        bool includeAttributes,
        bool isReadOnly)
    {
        var result = new Dictionary<string, object?>
        {
            ["value"] = property.GetValue(),
            ["type"] = JsonSchemaTypeMapper.ToJsonSchemaType(property.Type)
        };

        if (!isReadOnly && property.HasSetter)
        {
            result["isWritable"] = true;
        }

        if (includeAttributes)
        {
            var attributes = new Dictionary<string, object?>();
            foreach (var attribute in property.Attributes)
            {
                attributes[attribute.BrowseName] = attribute.GetValue();
            }

            if (attributes.Count > 0)
            {
                result["attributes"] = attributes;
            }
        }

        return result;
    }

    internal static string? TryGetSubjectPath(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        IInterceptorSubject rootSubject)
    {
        if (subject.Subject == rootSubject)
        {
            return "";
        }

        if (subject.Parents.Length > 0)
        {
            var parent = subject.Parents[0];
            return parent.Property.TryGetPath(pathProvider, rootSubject, parent.Index);
        }

        return null;
    }

    internal static Dictionary<string, object?> BuildSubjectNode(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        IInterceptorSubject rootSubject,
        McpServerConfiguration configuration,
        bool includeProperties,
        bool includeAttributes)
    {
        var node = new Dictionary<string, object?>();

        var subjectPath = TryGetSubjectPath(subject, pathProvider, rootSubject);
        if (subjectPath is not null)
        {
            node["$path"] = subjectPath;
        }

        ApplyEnrichments(node, subject, configuration);
        ApplyProperties(node, subject, pathProvider, includeProperties, includeAttributes, configuration.IsReadOnly);

        return node;
    }

    internal static void ApplyEnrichments(
        Dictionary<string, object?> node,
        RegisteredSubject subject,
        McpServerConfiguration configuration)
    {
        foreach (var enricher in configuration.SubjectEnrichers)
        {
            foreach (var kvp in enricher.GetSubjectEnrichments(subject))
            {
                node[kvp.Key] = kvp.Value;
            }
        }
    }

    internal static void FilterEnrichments(
        Dictionary<string, object?> node,
        bool includeMethods,
        bool includeInterfaces)
    {
        if (!includeMethods)
        {
            node.Remove("$methods");
        }

        if (!includeInterfaces)
        {
            node.Remove("$interfaces");
        }
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

    internal static void ApplyProperties(
        Dictionary<string, object?> node,
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        bool includeProperties,
        bool includeAttributes,
        bool isReadOnly)
    {
        if (!includeProperties)
        {
            return;
        }

        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute || !pathProvider.IsPropertyIncluded(property) || property.CanContainSubjects)
            {
                continue;
            }

            var segment = pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;
            node[segment] = BuildPropertyValue(property, includeAttributes, isReadOnly);
        }
    }
}

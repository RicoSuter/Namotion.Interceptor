using System.Text.Json;
using Namotion.Interceptor.Mcp.Models;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Shared helper methods for MCP tool implementations.
/// </summary>
internal static class McpToolHelper
{
    /// <summary>
    /// The error response for a path that did not resolve: the syntax error when <paramref name="path"/> is malformed,
    /// otherwise "Path not found".
    /// </summary>
    internal static object GetPathNotFoundError(PathProviderBase pathProvider, string path)
        => pathProvider.TryParsePath(path, out _, out var error)
            ? new { error = $"Path not found: {path}" }
            : new { error = $"Invalid path syntax: {error}" };

    internal static bool ShouldExcludeByType(RegisteredSubject subject, Type[] excludeTypes, string[]? requestExcludeTypes)
    {
        var subjectType = subject.Subject.GetType();

        foreach (var excludedType in excludeTypes)
        {
            if (excludedType.IsAssignableFrom(subjectType))
            {
                return true;
            }
        }

        if (requestExcludeTypes is not null && requestExcludeTypes.Length > 0)
        {
            var interfaces = subjectType.GetInterfaces();
            foreach (var name in requestExcludeTypes)
            {
                if (subjectType.FullName == name || subjectType.Name == name)
                {
                    return true;
                }

                if (interfaces.Any(i => i.FullName == name || i.Name == name))
                {
                    return true;
                }
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
        bool includeInterfaces,
        IReadOnlySet<string>? propertyNames = null)
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

        // A property selection implies including properties.
        Dictionary<string, SubjectNodeProperty>? properties = null;
        if (includeProperties || propertyNames is not null)
        {
            properties = new Dictionary<string, SubjectNodeProperty>();
            foreach (var property in subject.Properties)
            {
                if (TryGetSelectableSegment(property, pathProvider) is not { } segment ||
                    (propertyNames is not null && !propertyNames.Contains(segment)))
                {
                    continue;
                }

                properties[segment] = BuildScalarPropertyDto(property, configuration, includeAttributes);
            }
        }

        return new SubjectNode
        {
            Path = path,
            Type = type,
            Enrichments = enrichments.Count > 0 ? enrichments : null,
            Methods = methods,
            Interfaces = interfaces,
            Properties = properties
        };
    }

    /// <summary>
    /// Reads the optional <c>properties</c> selection. Returns the error response when it is present but not an
    /// array of strings, otherwise null.
    /// </summary>
    internal static object? TryReadPropertyNames(JsonElement input, out HashSet<string>? propertyNames)
    {
        propertyNames = null;
        if (!input.TryGetProperty("properties", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return InvalidPropertiesError();
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return InvalidPropertiesError();
            }

            names.Add(item.GetString()!);
        }

        propertyNames = names;
        return null;

        static object InvalidPropertiesError() => new { error = "Parameter 'properties' must be an array of property names." };
    }

    /// <summary>
    /// The name a scalar property is listed and selected under: its path segment, else its browse name. Null for an
    /// attribute, a property holding subjects or one the path provider excludes.
    /// </summary>
    internal static string? TryGetSelectableSegment(RegisteredSubjectProperty property, PathProviderBase pathProvider) =>
        property.IsAttribute || !pathProvider.IsPropertyIncluded(property) || property.CanContainSubjects
            ? null
            : pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

    /// <summary>
    /// Resolves <paramref name="segment"/> to a scalar property of <paramref name="subject"/> that the path provider
    /// exposes, or null when there is none.
    /// </summary>
    internal static RegisteredSubjectProperty? TryGetScalarProperty(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        string segment)
    {
        var property = pathProvider.TryGetPropertyFromSegment(subject, segment);
        return property is null ||
               property.IsAttribute ||
               property.CanContainSubjects ||
               !pathProvider.IsPropertyIncluded(property)
            ? null
            : property;
    }

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

    private static ScalarProperty BuildScalarPropertyDto(
        RegisteredSubjectProperty property,
        McpServerConfiguration configuration,
        bool includeAttributes)
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
            property.GetValue(),
            JsonSchemaTypeMapper.ToJsonSchemaType(property.Type) ?? "object",
            IsWritable: IsWritable(property, configuration),
            Attributes: attributes);
    }

    /// <summary>
    /// Whether set_property may write the property: <see cref="GetWriteError"/> returns null for it.
    /// </summary>
    internal static bool IsWritable(RegisteredSubjectProperty property, McpServerConfiguration configuration) =>
        !configuration.IsReadOnly && HasPublicSetter(property) && configuration.PropertyWriteFilter?.Invoke(property) is null;

    /// <summary>
    /// The error set_property returns for the property, or null when it may be written: the server is read-only, the
    /// property has no public setter, or the <see cref="McpServerConfiguration.PropertyWriteFilter"/> refuses it.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="path">The path of the property as the client gave it, for the message.</param>
    /// <param name="configuration">The configuration.</param>
    internal static string? GetWriteError(RegisteredSubjectProperty property, string path, McpServerConfiguration configuration)
    {
        if (configuration.IsReadOnly)
        {
            return "Server is in read-only mode.";
        }

        if (!HasPublicSetter(property))
        {
            return $"Property is not writable: {path}";
        }

        return configuration.PropertyWriteFilter?.Invoke(property) is { } reason
            ? $"Property is not writable: {path}. {reason}"
            : null;
    }

    // TODO: Clean up when HasSetter in registry respects access modifiers (see #102)
    private static bool HasPublicSetter(RegisteredSubjectProperty property) =>
        property.HasSetter && property.Reference.Metadata.PropertyInfo?.SetMethod?.IsPublic != false;

    private static string[]? ToStringArray(object? value)
    {
        if (value is string[] array) return array;
        if (value is IEnumerable<object?> enumerable)
            return enumerable.Select(item => item?.ToString() ?? "").ToArray();
        return null;
    }
}

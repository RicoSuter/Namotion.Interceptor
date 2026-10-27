using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
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
            var interfaces = TypeInterfaces.Get(subjectType);
            foreach (var name in requestExcludeTypes)
            {
                if (subjectType.FullName == name || subjectType.Name == name)
                {
                    return true;
                }

                foreach (var interfaceType in interfaces)
                {
                    if (interfaceType.FullName == name || interfaceType.Name == name)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the node of a subject whose path, from <see cref="TryGetSubjectPath"/>, is <paramref name="path"/>.
    /// </summary>
    internal static SubjectNode BuildSubjectNodeDto(
        RegisteredSubject subject,
        string? path,
        PathProviderBase pathProvider,
        McpServerConfiguration configuration,
        bool includeProperties,
        bool includeAttributes,
        bool includeMethods,
        bool includeInterfaces,
        IReadOnlySet<string>? propertyNames = null)
    {
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

    /// <summary>
    /// Resolves a path from the subject through the path provider to a property that a filter can read: every property
    /// on the way is one the provider includes and is owned by a shown subject, and the last one is no attribute and has
    /// no index. A property that holds subjects is one a filter can read, and its value is the subject,
    /// the collection or the dictionary, or null.
    /// </summary>
    internal static RegisteredSubjectProperty? TryGetFilterPropertyFromPath(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        string path,
        McpVisibility visibility)
    {
        if (visibility.GetListingPathProvider(pathProvider).TryGetPropertyFromPath(subject, path, includedPropertiesOnly: true, createMissingSubject: null) is not
            { Index: null, Property: var property })
        {
            return null;
        }

        return property.IsAttribute ? null : property;
    }

    /// <summary>
    /// The path the tools write for the subject from <paramref name="rootSubject"/>, after the configuration's
    /// <see cref="McpServerConfiguration.PathPrefix"/>: for the root the prefix, or null when it is empty, otherwise null
    /// when the registry writes no path. With an <see cref="McpServerConfiguration.AccessPolicy"/> also null when a
    /// property the path is written through is not readable.
    /// </summary>
    internal static string? TryGetSubjectPath(
        RegisteredSubject subject, McpServerConfiguration configuration, IInterceptorSubject rootSubject)
    {
        var pathPrefix = configuration.PathPrefix;
        if (subject.Subject == rootSubject)
        {
            return pathPrefix.Length > 0 ? pathPrefix : null;
        }

        var parents = subject.Parents;
        if (parents.Length == 0)
        {
            return null;
        }

        var parent = parents[0];
        var path = configuration.AccessPolicy is null
            ? parent.Property.TryGetPath(configuration.ToolPathProvider, rootSubject, parent.Index)
            : TryGetReadablePath(parent.Property, parent.Index, configuration, rootSubject);
        return path is not null ? $"{pathPrefix}{path}" : null;
    }

    // The chain is checked in the walk that writes the text, so a subject moved on it while a call runs cannot put the
    // name of a hidden property into the text. The resolution provider writes the listing provider's segments and
    // includes exactly what the policy allows to read; the listing provider's own inclusion applies to the holding
    // property only, as without a policy.
    private static string? TryGetReadablePath(
        RegisteredSubjectProperty property, object? index, McpServerConfiguration configuration, IInterceptorSubject rootSubject) =>
        configuration.ToolPathProvider.IsPropertyIncluded(property)
            ? property.TryGetPath(configuration.ResolutionPathProvider, rootSubject, index, includedPropertiesOnly: true)
            : null;

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
                if (TryGetListedAttributeValue(attribute, configuration, out var value))
                {
                    attributes.Add(new PropertyAttribute(attribute.BrowseName, value));
                }
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
    /// Whether the tools list the attribute of a listed property, and its value when they do: the policy allows to read
    /// it, and it has a value or a setter.
    /// </summary>
    internal static bool TryGetListedAttributeValue(
        RegisteredSubjectProperty attribute, McpServerConfiguration configuration, out object? value)
    {
        if (!configuration.IsAllowed(attribute, McpAccessActions.Read))
        {
            value = null;
            return false;
        }

        // A read-only attribute without a value says nothing about its property; a writable one can be set.
        value = attribute.GetValue();
        return value is not null || attribute.HasSetter;
    }

    /// <summary>
    /// Whether set_property may write the property: <see cref="GetWriteError"/> returns null for it.
    /// </summary>
    internal static bool IsWritable(RegisteredSubjectProperty property, McpServerConfiguration configuration) =>
        !configuration.IsReadOnly && property.HasPublicSetter && configuration.IsAllowed(property, McpAccessActions.Write);

    /// <summary>
    /// The error set_property returns for the property, or null when it may be written: the server is read-only, the
    /// property has no public setter, or the <see cref="McpServerConfiguration.AccessPolicy"/> does not allow writing it.
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

        if (!property.HasPublicSetter)
        {
            return $"Property is not writable: {path}";
        }

        return configuration.IsAllowed(property, McpAccessActions.Write)
            ? null
            : $"Property is not writable: {path}. The access policy does not allow it.";
    }

    private static string[]? ToStringArray(object? value)
    {
        if (value is string[] array) return array;
        if (value is IEnumerable<object?> enumerable)
            return enumerable.Select(item => item?.ToString() ?? "").ToArray();
        return null;
    }
}

using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Maps properties using an IPathProvider for inclusion and browse names.
/// Provides default reference type of "HasProperty".
/// </summary>
public class PathProviderOpcUaNodeMapper : IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>
{
    private readonly IPathProvider _pathProvider;

    public PathProviderOpcUaNodeMapper(IPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    /// <inheritdoc />
    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out OpcUaPropertyMapping? mapping)
    {
        if (!_pathProvider.IsPropertyIncluded(property))
        {
            mapping = null;
            return false;
        }

        var browseName = _pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;
        var referenceType = property.IsAttribute ? "HasProperty" : null;

        mapping = new OpcUaPropertyMapping
        {
            BrowseName = browseName,
            ReferenceType = referenceType
        };
        return true;
    }

    /// <inheritdoc />
    public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject root,
        OpcUaLookupKey key,
        CancellationToken cancellationToken)
    {
        var browseName = key.Reference.BrowseName.Name;

        foreach (var property in root.Properties)
        {
            if (property.IsAttribute)
                continue;

            if (!_pathProvider.IsPropertyIncluded(property))
                continue;

            var segment = _pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;
            if (segment == browseName)
            {
                return new ValueTask<RegisteredSubjectProperty?>(property);
            }
        }

        return new ValueTask<RegisteredSubjectProperty?>(result: null);
    }
}

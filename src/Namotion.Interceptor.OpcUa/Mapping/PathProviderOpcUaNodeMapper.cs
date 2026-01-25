using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Maps properties using an IPathProvider for inclusion and browse names.
/// Provides default reference type of "HasProperty".
/// </summary>
public class PathProviderOpcUaNodeMapper : IOpcUaNodeMapper
{
    private readonly IPathProvider _pathProvider;

    /// <summary>
    /// Creates a new path provider-based node mapper.
    /// </summary>
    /// <param name="pathProvider">The path provider for property inclusion and naming.</param>
    public PathProviderOpcUaNodeMapper(IPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    /// <inheritdoc />
    public OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        if (!_pathProvider.IsPropertyIncluded(property))
        {
            return null;
        }

        // Use PathProvider segment, or fall back to property.BrowseName
        // (which is AttributeName for attributes, Name for regular properties)
        var browseName = _pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

        // Default ReferenceType: "HasProperty" for attributes, null for others
        // This allows CompositeNodeMapper to fill in from other sources
        var referenceType = property.IsAttribute ? "HasProperty" : null;

        return new OpcUaNodeConfiguration
        {
            BrowseName = browseName,
            ReferenceType = referenceType
        };
    }

    /// <inheritdoc />
    public Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        var browseName = nodeReference.BrowseName.Name;

        foreach (var property in subject.Properties)
        {
            if (!_pathProvider.IsPropertyIncluded(property))
            {
                continue;
            }

            var segment = _pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;
            if (segment == browseName)
            {
                return Task.FromResult<RegisteredSubjectProperty?>(property);
            }
        }

        return Task.FromResult<RegisteredSubjectProperty?>(null);
    }
}

using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Maps properties using an IPathProvider for inclusion and browse names.
/// Provides default reference type of "HasProperty".
/// </summary>
public class OpcUaPathProviderMapper : IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>
{
    private readonly IPathProvider _pathProvider;

    public OpcUaPathProviderMapper(IPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Forwards <see cref="PathProviderBase.Version"/> when the provider derives from it, and is 0 for any other <see cref="IPathProvider"/>.
    /// </remarks>
    public virtual long Version => (_pathProvider as PathProviderBase)?.Version ?? 0;

    /// <inheritdoc />
    public virtual bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out OpcUaPropertyMapping? mapping)
    {
        if (!_pathProvider.TryGetPropertySegment(property, out var browseName))
        {
            mapping = null;
            return false;
        }

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
        OpcUaLookupKey key,
        RegisteredSubject subject,
        CancellationToken cancellationToken)
    {
        var browseName = key.Reference.BrowseName.Name;

        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute)
                continue;

            if (_pathProvider.TryGetPropertySegment(property, out var segment) && segment == browseName)
            {
                return new ValueTask<RegisteredSubjectProperty?>(property);
            }
        }

        return new ValueTask<RegisteredSubjectProperty?>(result: null);
    }
}

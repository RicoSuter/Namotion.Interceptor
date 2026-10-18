using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.OpcUa;

public class StateAttributeOpcUaPathProvider : PathProviderBase
{
    // The metadata subject of a stored subject, a folder or the root is left out with everything below it, so that a
    // device gets no metadata node.
    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
        => property.Name != IMetadataSubject.PropertyName &&
           (property.TryGetAttribute(KnownAttributes.State) is not null || property.CanContainSubjects);

    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        if (property.TryGetAttribute(KnownAttributes.State) is not null)
        {
            return property.Name;
        }

        return property.CanContainSubjects ? property.Name : null;
    }
}

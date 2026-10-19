using HomeBlaze.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.OpcUa;

public class StateAttributeOpcUaPathProvider : PathProviderBase
{
    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
        => property.TryGetAttribute(KnownAttributes.State) is not null || property.CanContainSubjects;

    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        if (property.TryGetAttribute(KnownAttributes.State) is not null)
        {
            return property.Name;
        }

        return property.CanContainSubjects ? property.Name : null;
    }
}

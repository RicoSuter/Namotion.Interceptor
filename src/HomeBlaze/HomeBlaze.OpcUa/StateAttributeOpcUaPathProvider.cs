using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.OpcUa;

public class StateAttributeOpcUaPathProvider : PathProviderBase
{
    public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        segment = property.TryGetAttribute(KnownAttributes.State) is not null || property.CanContainSubjects ? property.Name : null;
        return segment is not null;
    }
}

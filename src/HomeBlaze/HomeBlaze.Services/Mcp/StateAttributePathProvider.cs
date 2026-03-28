using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Services.Mcp;

/// <summary>
/// Exposes [State] properties via MCP. Uses state metadata name as path segment.
/// </summary>
public class StateAttributePathProvider : PathProviderBase
{
    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
        => property.TryGetAttribute(KnownAttributes.State) is not null;

    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        var metadata = property.TryGetAttribute(KnownAttributes.State)?.GetValue() as StateMetadata;
        return metadata?.Name ?? property.Name;
    }
}

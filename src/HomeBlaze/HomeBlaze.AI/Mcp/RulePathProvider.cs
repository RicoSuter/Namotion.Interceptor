using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Services.Metadata;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// The path provider that the path patterns of roles are matched with: the segments of
/// <see cref="StateAttributePathProvider"/>, and the full name of every other attribute, such as <c>Temperature@Unit</c>,
/// and of the metadata of a subject, <c>HomeBlaze:Metadata</c>. The tools list metadata only while it has a value, but a
/// rule names a member by the same path whatever its value, so that a write of metadata that does not exist yet is
/// decided as a later write is.
/// </summary>
public class RulePathProvider : StateAttributePathProvider
{
    public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        if (base.TryGetPropertySegment(property, out segment))
        {
            return true;
        }

        segment = property.IsAttribute || property.IsMetadata() ? property.Name : null;
        return segment is not null;
    }
}

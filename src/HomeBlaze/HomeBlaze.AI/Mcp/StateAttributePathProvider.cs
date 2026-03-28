using HomeBlaze.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Exposes [State] properties via MCP. Uses slash separator for InlinePaths compatibility
/// (file names with dots like "Dashboard.md" are valid single segments with slash notation).
/// </summary>
public class StateAttributePathProvider : PathProviderBase
{
    public override char PathSeparator => '/';

    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
        => property.TryGetAttribute(KnownAttributes.State) is not null;

    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        if (property.TryGetAttribute(KnownAttributes.State) is not null)
        {
            return property.Name;
        }

        return property.CanContainSubjects ? property.Name : null;
    }

    public override RegisteredSubjectProperty? TryGetPropertyFromSegment(
        RegisteredSubject subject, string segment)
    {
        // First try base lookup (matches State properties and [InlinePaths] fallback)
        var result = base.TryGetPropertyFromSegment(subject, segment);
        if (result is not null)
        {
            return result;
        }

        // Also match structural properties (CanContainSubjects) by name,
        // so navigation through Children dictionaries works even when
        // the property doesn't have a [State] attribute.
        foreach (var property in subject.Properties)
        {
            if (property.CanContainSubjects && property.Name == segment)
            {
                return property;
            }
        }

        return null;
    }
}

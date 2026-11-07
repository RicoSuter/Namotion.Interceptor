using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Services.Metadata;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// The path provider that the path patterns of roles are matched with: the segments of
/// <see cref="StateAttributePathProvider"/>, and for every other attribute the name of its property, <c>@</c> and its own
/// name, such as <c>Temperature@Unit</c>, also for one declared in C#, and for the metadata of a subject
/// <c>HomeBlaze:Metadata</c>. The tools list metadata only while it has a value, but a
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

        if (property.IsAttribute)
        {
            segment = GetAttributeName(property);
        }
        else
        {
            segment = property.IsMetadata() ? property.Name : null;
        }

        return segment is not null;
    }

    // Property@Attribute, nested for an attribute of an attribute, as the members selector of a rule names it: an
    // attribute declared in C# has a property name of its own, such as Temperature_Unit, that no tool shows. Null when the
    // chain of properties is broken, as the registry tolerates.
    private static string? GetAttributeName(RegisteredSubjectProperty attribute)
    {
        var name = attribute.BrowseName;
        var remainingSteps = attribute.Parent.Properties.Length;
        var current = attribute;
        while (current.IsAttribute)
        {
            var property = current.Parent.TryGetProperty(current.AttributeMetadata.PropertyName);
            if (property is null || --remainingSteps < 0)
            {
                return null;
            }

            name = string.Concat(property.IsAttribute ? property.BrowseName : property.Name, "@", name);
            current = property;
        }

        return name;
    }
}

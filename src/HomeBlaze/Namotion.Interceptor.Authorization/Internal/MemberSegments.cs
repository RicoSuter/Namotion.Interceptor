using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Internal;

internal static class MemberSegments
{
    /// <summary>The segment of the property as the provider writes it, or its browse name when the provider gives none.</summary>
    public static string GetSegment(PathProviderBase pathProvider, RegisteredSubjectProperty property)
        => pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

    /// <summary>
    /// For an attribute, the segment of the property it belongs to, <c>@</c> and its name, so <c>Volume@Unit</c> and,
    /// for an attribute of that attribute, <c>Volume@Unit@Source</c>; null for a property. Allocates.
    /// </summary>
    public static string? TryGetAttributeSegment(PathProviderBase pathProvider, RegisteredSubjectProperty property)
    {
        if (!property.IsAttribute)
        {
            return null;
        }

        var owner = property.GetAttributedProperty();
        return (TryGetAttributeSegment(pathProvider, owner) ?? GetSegment(pathProvider, owner)) + "@" + property.BrowseName;
    }

    /// <summary>The member as the last link of a member path.</summary>
    public static PathLink GetMemberLink(PathProviderBase pathProvider, RegisteredSubjectProperty member)
        => new(GetSegment(pathProvider, member), null, null, TryGetAttributeSegment(pathProvider, member));
}

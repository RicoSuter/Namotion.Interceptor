using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Internal;

internal static class MemberSegments
{
    /// <summary>
    /// The segment of the property as the registry's path writer spells it: the provider's segment, or the browse name
    /// when the provider gives none. An attribute is spelled the same way, so attribute paths follow the registry's syntax.
    /// </summary>
    public static string GetSegment(PathProviderBase pathProvider, RegisteredSubjectProperty property)
        => pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

    /// <summary>The member as the last link of a member path.</summary>
    public static PathLink GetMemberLink(PathProviderBase pathProvider, RegisteredSubjectProperty member)
        => new(GetSegment(pathProvider, member), null, null);
}

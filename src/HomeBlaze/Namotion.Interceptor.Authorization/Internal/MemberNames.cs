using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization.Internal;

internal static class MemberNames
{
    /// <summary>The name a member glob matches: the property name, or <c>Property@Attribute</c> for an attribute, nested for an attribute of an attribute.</summary>
    public static string GetName(RegisteredSubjectProperty member)
        => member.IsAttribute ? GetName(member.GetAttributedProperty()) + "@" + member.BrowseName : member.Name;
}

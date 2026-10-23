using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization;

/// <summary>Supplies the tags of a member, which rules select on with <see cref="PermissionRule.Tags"/>.</summary>
public interface IMemberTagProvider
{
    /// <summary>
    /// Gets the tags of a member, compared ignoring case.
    /// </summary>
    /// <remarks>
    /// Called on every evaluation of a rule with tags and possibly concurrently, so an implementation caches what is
    /// expensive to derive. Return an array where possible; the policy then compares tags without allocating. Never
    /// returns null; return an empty array for a member without tags.
    /// </remarks>
    IReadOnlyCollection<string> GetTags(RegisteredSubjectProperty member);
}

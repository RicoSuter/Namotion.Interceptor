using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization;

/// <summary>Supplies the tags of a member, which rules select on with <see cref="PermissionRule.Tags"/>.</summary>
public interface IMemberTagProvider
{
    /// <summary>
    /// Gets the tags of a member, compared ignoring case; never a default array, an empty one for a member without tags.
    /// </summary>
    /// <remarks>
    /// Called on every evaluation of a rule with tags and possibly concurrently, so an implementation caches what is
    /// expensive to derive.
    /// </remarks>
    ImmutableArray<string> GetTags(RegisteredSubjectProperty member);
}

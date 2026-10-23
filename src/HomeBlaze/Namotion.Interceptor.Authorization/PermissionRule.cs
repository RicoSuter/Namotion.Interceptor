using System.Collections.Immutable;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// Selectors and actions of one rule. The rule applies to a member when every selector that is not empty matches:
/// a path pattern matches the subject's path or an ancestor's path, the subject is assignable to one of the type
/// names, the member's name matches one of the member globs, and the member carries all the tags. Whether the rule
/// allows or denies is decided by the list of <see cref="AuthorizationRole"/> it is in.
/// </summary>
public sealed record PermissionRule
{
    /// <summary>Gets the path patterns; empty for everywhere. See the README for the syntax.</summary>
    /// <remarks>
    /// A subject reachable by several paths matches when any of them does. When the paths of a subject cannot all be
    /// listed (a path cannot be written, the subject has parents but no path to a root, or it has more than 64 paths or
    /// too densely connected parents), an allow rule matches only through the paths that were listed, at most the first
    /// 64, and every deny rule with paths matches.
    /// </remarks>
    public ImmutableArray<string> Paths { get; init; } = [];

    /// <summary>Gets the full or simple names of types or interfaces; empty for any type.</summary>
    public ImmutableArray<string> Types { get; init; } = [];

    /// <summary>Gets the member name globs, <c>*</c> matching any text; empty for all members. An attribute is named <c>Property@Attribute</c>.</summary>
    public ImmutableArray<string> Members { get; init; } = [];

    /// <summary>Gets the tags a member must all carry; empty for any.</summary>
    public ImmutableArray<string> Tags { get; init; } = [];

    /// <summary>Gets the actions the rule applies to; at least one.</summary>
    public ImmutableArray<PermissionAction> Actions { get; init; } = [];
}

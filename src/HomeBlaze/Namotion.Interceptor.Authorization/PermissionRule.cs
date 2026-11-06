using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Authorization.Internal;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// Selectors and actions of one rule. The rule applies to an action on a member when the action is listed and every
/// selector that is not empty matches. Whether the rule allows or denies is decided by the list of
/// <see cref="AuthorizationRole"/> it is in. The lists are never default.
/// </summary>
/// <remarks>Two instances are equal when every property is equal element by element, tags and actions ignoring case like matching, the other selector values exactly.</remarks>
[JsonConverter(typeof(PermissionRuleJsonConverter))]
public sealed record PermissionRule
{
    /// <summary>
    /// Gets the path patterns, each matching a member's whole path, the path of its subject followed by the member's
    /// segment; empty for everywhere. Patterns use the syntax of the path provider given to
    /// <see cref="AuthorizationDefinition.Create"/>; <c>*</c> alone matches one segment, <c>**</c> zero or more, and
    /// <c>*</c> inside a segment or index is a glob over its text. A pattern for a branch ends in <c>**</c>.
    /// </summary>
    /// <remarks>
    /// A subject reachable by several paths matches when any of them does. When the paths of a subject cannot all be
    /// listed, an allow rule matches only through the paths that were listed and every deny rule with paths matches.
    /// </remarks>
    public ImmutableArray<string> Paths { get; init => field = value.IsDefault ? [] : value; } = [];

    /// <summary>Gets the full or simple names of types or interfaces the member's subject is assignable to; empty for any type.</summary>
    /// <remarks>
    /// In a trimmed application, trimming can remove the implementation of an interface that no code uses, so a rule
    /// naming such an interface may not match.
    /// </remarks>
    public ImmutableArray<string> Types { get; init => field = value.IsDefault ? [] : value; } = [];

    /// <summary>Gets the member name globs, <c>*</c> matching any text; empty for all members. An attribute is named <c>Property@Attribute</c>.</summary>
    public ImmutableArray<string> Members { get; init => field = value.IsDefault ? [] : value; } = [];

    /// <summary>Gets the tags a member must all carry, compared ignoring case; empty for any.</summary>
    public ImmutableArray<string> Tags { get; init => field = value.IsDefault ? [] : value; } = [];

    /// <summary>Gets the actions the rule applies to; at least one.</summary>
    public ImmutableArray<PermissionAction> Actions { get; init => field = value.IsDefault ? [] : value; } = [];

    /// <summary>Whether every property of <paramref name="other"/> is equal element by element, in order.</summary>
    public bool Equals(PermissionRule? other)
        => ReferenceEquals(this, other) ||
           (other is not null &&
            SequenceEquality.AreEqual(Paths, other.Paths, StringComparer.Ordinal) &&
            SequenceEquality.AreEqual(Types, other.Types, StringComparer.Ordinal) &&
            SequenceEquality.AreEqual(Members, other.Members, StringComparer.Ordinal) &&
            SequenceEquality.AreEqual(Tags, other.Tags, StringComparer.OrdinalIgnoreCase) &&
            SequenceEquality.AreEqual(Actions, other.Actions));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        SequenceEquality.AddTo(ref hash, Paths, StringComparer.Ordinal);
        SequenceEquality.AddTo(ref hash, Types, StringComparer.Ordinal);
        SequenceEquality.AddTo(ref hash, Members, StringComparer.Ordinal);
        SequenceEquality.AddTo(ref hash, Tags, StringComparer.OrdinalIgnoreCase);
        SequenceEquality.AddTo(ref hash, Actions);
        return hash.ToHashCode();
    }
}

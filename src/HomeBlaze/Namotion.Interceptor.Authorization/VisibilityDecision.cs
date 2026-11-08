using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// The answer of <see cref="AuthorizationPolicy.ExplainVisibility(RegisteredSubject)"/>: whether the subject is
/// visible, and otherwise why.
/// </summary>
/// <remarks>
/// The answer holds for one instant of the graph. When the parents or attachment of a subject the evaluation read,
/// on the members' paths or on the way to a root, kept changing during every attempt, the answer fails closed:
/// <see cref="IsVisible"/> and <see cref="HasReadableMember"/> are false and <see cref="DeniedProperty"/> is null,
/// whatever the rules say. An instance that the registry of its context does not hold, such as a detached one, answers
/// the same way.
/// </remarks>
public readonly record struct VisibilityDecision
{
    internal VisibilityDecision(bool isVisible, bool hasReadableMember, RegisteredSubjectProperty? deniedProperty)
    {
        IsVisible = isVisible;
        HasReadableMember = hasReadableMember;
        DeniedProperty = deniedProperty;
    }

    /// <summary>Gets whether at least one member is readable and the subject is reachable from a root through readable subject-holding properties.</summary>
    public bool IsVisible { get; }

    /// <summary>Gets whether at least one member is readable.</summary>
    public bool HasReadableMember { get; }

    /// <summary>
    /// Gets, when the subject has a readable member but is not reachable, a subject-holding property on the way that is
    /// not readable; null when the subject is visible or when the walk to a root meets no unreadable link, as for
    /// subjects that only hold each other in a cycle without a root.
    /// </summary>
    public RegisteredSubjectProperty? DeniedProperty { get; }
}

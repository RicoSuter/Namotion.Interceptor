using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// The answer of <see cref="AuthorizationPolicy.Explain(RegisteredSubject)"/>: whether the subject is readable, and
/// otherwise why.
/// </summary>
/// <remarks>
/// The answer holds for one instant of the graph. When the parents or attachment of a subject the evaluation read,
/// on the members' paths or on the way to a root, kept changing during every attempt, the answer fails closed:
/// <c>IsReadable</c> and <c>HasReadableMember</c> are false and <c>DeniedProperty</c> is null, whatever the rules say.
/// </remarks>
/// <param name="IsReadable">Whether at least one member is readable and the subject is reachable from the root through readable subject-holding properties.</param>
/// <param name="HasReadableMember">Whether at least one member is readable.</param>
/// <param name="DeniedProperty">When the subject has a readable member but is not reachable, a subject-holding property on the way that is not readable; null when the subject is readable or when the walk to a root meets no unreadable link, as for subjects that only hold each other in a cycle without a root.</param>
public readonly record struct SubjectReadDecision(
    bool IsReadable,
    bool HasReadableMember,
    RegisteredSubjectProperty? DeniedProperty);

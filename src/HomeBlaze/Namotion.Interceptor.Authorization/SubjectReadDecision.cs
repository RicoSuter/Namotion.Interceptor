using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// The answer of <see cref="AuthorizationPolicy.Explain(RegisteredSubject)"/>: whether the subject is readable, and
/// otherwise why.
/// </summary>
/// <param name="IsReadable">Whether at least one member is readable and the subject is reachable from the root through readable subject-holding properties.</param>
/// <param name="HasReadableMember">Whether at least one member is readable.</param>
/// <param name="DeniedProperty">When the subject has a readable member but is not reachable, a subject-holding property on the way that is not readable; otherwise null.</param>
public readonly record struct SubjectReadDecision(
    bool IsReadable,
    bool HasReadableMember,
    RegisteredSubjectProperty? DeniedProperty);

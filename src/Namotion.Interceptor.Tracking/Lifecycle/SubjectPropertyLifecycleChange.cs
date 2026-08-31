using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <param name="Subject">Gets the subject where a property reference pointing to it has been changed.</param>
/// <param name="Property">Gets the property.</param>
public record struct SubjectPropertyLifecycleChange(
    IInterceptorSubject Subject,
    PropertyReference Property)
{
    /// <summary>Gets the exact context whose committed lifecycle produced this change.</summary>
    public IInterceptorSubjectContext? Context { get; init; }

    /// <summary>Gets the monotonic publication revision of the complete property projection.</summary>
    public long Revision { get; init; }

    /// <summary>Gets the captured metadata for <see cref="Property"/>.</summary>
    public SubjectPropertyMetadata Metadata { get; init; }

    /// <summary>Gets the complete committed child projection of <see cref="Property"/>.</summary>
    public ImmutableArray<(IInterceptorSubject Subject, object? Index)> Children { get; init; }

    /// <summary>Gets the complete parent projection of each distinct child.</summary>
    public ImmutableArray<(IInterceptorSubject Subject, ImmutableArray<SubjectParent> Parents)> ChildSubjects { get; init; }
}

using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Contains information about a lifecycle change event for a subject.
/// </summary>
public readonly struct SubjectLifecycleChange
{
    /// <summary>Gets the exact context whose committed lifecycle produced this change.</summary>
    public IInterceptorSubjectContext? Context { get; init; }

    /// <summary>Gets the monotonic publication revision of the complete projections in this change.</summary>
    public long Revision { get; init; }

    /// <summary>Gets the subject where a property reference pointing to it has been changed.</summary>
    public required IInterceptorSubject Subject { get; init; }

    /// <summary>Gets the complete property metadata projection of <see cref="Subject"/>.</summary>
    public ImmutableArray<SubjectPropertyMetadata> Properties { get; init; }

    /// <summary>Gets the complete committed parent projection of <see cref="Subject"/>.</summary>
    public ImmutableArray<SubjectParent> Parents { get; init; }

    /// <summary>Gets the property which has been changed.</summary>
    public PropertyReference? Property { get; init; }

    /// <summary>Gets the complete committed child projection of <see cref="Property"/>.</summary>
    public ImmutableArray<(IInterceptorSubject Subject, object? Index)> PropertyChildren { get; init; }

    /// <summary>Gets the index defining the place of the subject in the property's dictionary or collection.</summary>
    public object? Index { get; init; }

    /// <summary>Gets the number of structural edge occurrences pointing to the referenced subject.</summary>
    public required int ReferenceCount { get; init; }

    /// <summary>True when the subject first entered the graph.</summary>
    public bool IsContextAttach { get; init; }

    /// <summary>True when a property reference to the subject was added.</summary>
    public bool IsPropertyReferenceAdded { get; init; }

    /// <summary>True when a property reference to the subject was removed.</summary>
    public bool IsPropertyReferenceRemoved { get; init; }

    /// <summary>True when the subject is leaving the graph.</summary>
    public bool IsContextDetach { get; init; }
}

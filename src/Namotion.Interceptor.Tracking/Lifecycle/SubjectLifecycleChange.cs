using System.Diagnostics.CodeAnalysis;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Contains information about a lifecycle change event for a subject.
/// </summary>
public readonly struct SubjectLifecycleChange
{
    [SetsRequiredMembers]
    internal SubjectLifecycleChange(
        IInterceptorSubject subject,
        PropertyReference? property,
        object? index,
        int referenceCount,
        bool isContextAttach,
        bool isPropertyReferenceAdded,
        bool isPropertyReferenceRemoved,
        bool isContextDetach,
        SubjectPropertyRelationship? relationship = null)
    {
        Subject = subject;
        Property = property;
        Index = index;
        ReferenceCount = referenceCount;
        IsContextAttach = isContextAttach;
        IsPropertyReferenceAdded = isPropertyReferenceAdded;
        IsPropertyReferenceRemoved = isPropertyReferenceRemoved;
        IsContextDetach = isContextDetach;
        Relationship = relationship;
    }

    /// <summary>Gets the subject where a property reference pointing to it has been changed.</summary>
    public required IInterceptorSubject Subject { get; init; }

    /// <summary>Gets the property which has been changed.</summary>
    public PropertyReference? Property { get; init; }

    /// <summary>Gets the index defining the place of the subject in the property's dictionary or collection.</summary>
    public object? Index { get; init; }

    /// <summary>
    /// Gets the exact parent-child occurrence associated with this membership transition, when materialized.
    /// </summary>
    public SubjectPropertyRelationship? Relationship { get; }

    /// <summary>Gets the number of properties pointing to the referenced subject.</summary>
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

using System.Collections.Immutable;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Contains information about a lifecycle change event for a subject.
/// </summary>
public readonly struct SubjectLifecycleChange
{
    /// <summary>Gets the subject where a property reference pointing to it has been changed.</summary>
    public required IInterceptorSubject Subject { get; init; }

    /// <summary>Gets the property which has been changed.</summary>
    public PropertyReference? Property { get; init; }

    /// <summary>Gets the index defining the place of the subject in the property's dictionary or collection; on attach, its first one when the value holds it more than once.</summary>
    /// <remarks>
    /// On detach, remove every stored index of the subject for the property: a subject held more than once has one
    /// per key or position and this is only one of them.
    /// </remarks>
    public object? Index { get; init; }

    /// <summary>
    /// Gets the later keys or positions of the subject in the property's value, in order, when an added property
    /// reference finds it at more than one; otherwise empty. Together with <see cref="Index"/> they are every key or
    /// position of the subject in the value.
    /// </summary>
    public ImmutableArray<object?> AdditionalIndices
    {
        get => field.IsDefault ? [] : field;
        init => field = value;
    }

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

    /// <summary>The interceptor that dispatches the removal of a property reference.</summary>
    internal LifecycleInterceptor? Interceptor { get; init; }
}

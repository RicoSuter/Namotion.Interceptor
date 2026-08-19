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

    /// <summary>Gets the index defining the place of the subject in the property's dictionary or collection.</summary>
    public object? Index { get; init; }

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

    /// <summary>
    /// Gets the property reference whose removal earlier in the same lifecycle batch scope left the subject
    /// without references, set only on the attach which re-adds one before that deferred detach is processed.
    /// The subject left and re-entered the graph, so <see cref="IsContextAttach"/> is false on such an attach
    /// even though the added reference is its only one. Internal because it exists for
    /// <see cref="ContextInheritanceHandler"/> to follow the subject to its new parent, which is the only
    /// reconciliation a deferred detach leaves undone.
    /// </summary>
    internal PropertyReference? MovedFromProperty { get; init; }
}

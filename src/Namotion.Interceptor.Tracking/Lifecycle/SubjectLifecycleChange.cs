using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Flags indicating which lifecycle events occurred (internal use only).
/// </summary>
[Flags]
internal enum LifecycleEventFlags : byte
{
    None = 0,
    ContextAttached = 1,
    PropertyReferenceAdded = 2,
    PropertyReferenceRemoved = 4,
    ContextDetached = 8
}

/// <summary>
/// Contains information about a lifecycle change event for a subject.
/// </summary>
public readonly struct SubjectLifecycleChange
{
    private readonly LifecycleEventFlags _flags;

    /// <summary>Gets the subject where a property reference pointing to it has been changed.</summary>
    public readonly IInterceptorSubject Subject;

    /// <summary>Gets the property which has been changed.</summary>
    public readonly PropertyReference? Property;

    /// <summary>Gets the index defining the place of the subject in the property's dictionary or collection.</summary>
    public readonly object? Index;

    /// <summary>Gets the number of properties pointing to the referenced subject.</summary>
    public readonly int ReferenceCount;

    /// <summary>True when the subject first entered the graph.</summary>
    public bool IsContextAttach
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & LifecycleEventFlags.ContextAttached) != 0;
    }

    /// <summary>True when a property reference to the subject was added.</summary>
    public bool IsPropertyReferenceAdded
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & LifecycleEventFlags.PropertyReferenceAdded) != 0;
    }

    /// <summary>True when a property reference to the subject was removed.</summary>
    public bool IsPropertyReferenceRemoved
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & LifecycleEventFlags.PropertyReferenceRemoved) != 0;
    }

    /// <summary>True when the subject is leaving the graph.</summary>
    public bool IsContextDetach
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & LifecycleEventFlags.ContextDetached) != 0;
    }

    /// <summary>
    /// Creates a lifecycle change with no event flags set (for testing/external use).
    /// </summary>
    public SubjectLifecycleChange(
        IInterceptorSubject subject,
        PropertyReference? property,
        object? index,
        int referenceCount)
    {
        Subject = subject;
        Property = property;
        Index = index;
        ReferenceCount = referenceCount;
        _flags = LifecycleEventFlags.None;
    }

    internal SubjectLifecycleChange(
        IInterceptorSubject subject,
        PropertyReference? property,
        object? index,
        int referenceCount,
        LifecycleEventFlags flags)
    {
        Subject = subject;
        Property = property;
        Index = index;
        ReferenceCount = referenceCount;
        _flags = flags;
    }
}

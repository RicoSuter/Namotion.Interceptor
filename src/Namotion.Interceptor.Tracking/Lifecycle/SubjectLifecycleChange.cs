using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Flags indicating which lifecycle events occurred (internal use only).
/// </summary>
[Flags]
internal enum LifecycleEventFlags : byte
{
    None = 0,
    Attached = 1,
    ReferenceAdded = 2,
    ReferenceRemoved = 4,
    Detached = 8
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
    public bool IsAttached
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & LifecycleEventFlags.Attached) != 0;
    }

    /// <summary>True when a property reference to the subject was added.</summary>
    public bool IsReferenceAdded
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & LifecycleEventFlags.ReferenceAdded) != 0;
    }

    /// <summary>True when a property reference to the subject was removed.</summary>
    public bool IsReferenceRemoved
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & LifecycleEventFlags.ReferenceRemoved) != 0;
    }

    /// <summary>True when the subject is leaving the graph.</summary>
    public bool IsDetached
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_flags & LifecycleEventFlags.Detached) != 0;
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

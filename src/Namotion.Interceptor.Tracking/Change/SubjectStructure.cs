using System.ComponentModel;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Structure reads and changes for derived property tracking: whether a subject is attached and registered, its
/// reference count, its parents and its registered properties. Infrastructure for the lifecycle and the registry.
/// </summary>
/// <remarks>
/// The structure of a subject is one dependency source, linked and versioned like a property. See "Structure
/// Cells" in docs/design/tracking-derived-properties.md.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SubjectStructure
{
    /// <summary>Records that the running derived getter reads the structure of <paramref name="subject"/>.</summary>
    /// <param name="subject">The subject. Called before its structure is read.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordRead(IInterceptorSubject subject)
    {
        if (PropertyAccessObservation.ReadObserver is DerivedPropertyRecorder recorder)
        {
            recorder.OnPropertyRead(Of(subject));
        }
    }

    /// <summary>Records that the running derived getter reads <paramref name="property"/>, like a read of its value.</summary>
    /// <param name="property">The property.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordPropertyRead(PropertyReference property)
    {
        PropertyAccessObservation.ReadObserver?.OnPropertyRead(property);
    }

    /// <summary>Recalculates the derived properties that read the structure of <paramref name="subject"/>.</summary>
    /// <param name="subject">The subject. Called after its structure changed.</param>
    public static void Changed(IInterceptorSubject subject)
    {
        TrackingWriteObserver.OnStructureChanged(subject);
    }

    /// <summary>
    /// The reference under which the structure of <paramref name="subject"/> is recorded and linked: it has no
    /// property name, so it is never a property of the subject.
    /// </summary>
    internal static PropertyReference Of(IInterceptorSubject subject) => new(subject, null!);

    internal static bool IsStructure(in PropertyReference reference) => reference.Name is null;
}

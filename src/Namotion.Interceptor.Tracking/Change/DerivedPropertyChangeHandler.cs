using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived property tracking and automatic recalculation using dependency recording.
/// A write requests the recalculation of its dependents once, after the whole write chain.
/// </summary>
/// <remarks>
/// The recalculation follows the ownership protocol of <see cref="DerivedPropertyOwnership"/>.
/// See docs/design/tracking-derived-properties.md for full concurrency analysis.
/// </remarks>
public class DerivedPropertyChangeHandler : IPropertyLifecycleHandler
{
    internal static bool IsRecordingDerivedProperty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => DerivedPropertyRecorder.IsEvaluatingOnThisThread;
    }

    public DerivedPropertyChangeHandler()
    {
        TrackingWriteObserver.EnsureRegistered();
    }

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        if (change.Property.Metadata.IsDerived)
        {
            DerivedPropertyOwnership.Attach(change.Property);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A detach issued by a lifecycle interceptor is ignored: the subject may still be attached to
    /// another graph, and the lifecycle detaches the derived properties itself once the subject left
    /// its last graph. A direct call detaches, unless it is made on a thread on which a lifecycle interceptor
    /// is detaching a subject's properties (for example by a lifecycle handler): such a call is ignored,
    /// whichever subject it is for.
    /// </remarks>
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        if (!SubjectLifecycleAttachments.IsLifecycleDetach && change.Property.Metadata.IsDerived)
        {
            DerivedPropertyOwnership.Detach(change.Property, onlyIfSubjectIsDetached: false);
        }
    }
}

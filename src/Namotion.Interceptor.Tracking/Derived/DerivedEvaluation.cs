using System.ComponentModel;

namespace Namotion.Interceptor.Tracking.Derived;

/// <summary>The evaluations of derived getters on the current thread. Infrastructure for the registry.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DerivedEvaluation
{
    /// <summary>
    /// The number of derived getter evaluations that run nested on this thread: 0 outside every evaluation, and one more
    /// for each recalculation that starts inside another. A suspended recording, while a write in a getter does
    /// lifecycle or queued work, does not count.
    /// </summary>
    public static int Depth => DerivedPropertyRecorder.EvaluationDepth;
}

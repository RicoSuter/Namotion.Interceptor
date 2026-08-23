using System.Diagnostics;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Debug-only detection for the callback contract: a lifecycle callback (an
/// <see cref="ILifecycleHandler"/> invocation or a subject attach/detach event) must not write a
/// structural property. The lifecycle publishes callbacks while it holds the topology gate in the
/// middle of reconciling an edge set, so a structural write from a callback re-enters the
/// reconciler on half-updated state. The depth is thread-local and shared across built-in
/// lifecycle instances, so a callback writing into another context's graph is detected too.
/// Release builds compile the call sites out and pay no check.
/// </summary>
/// <remarks>
/// Property lifecycle callbacks (<see cref="IPropertyLifecycleHandler"/>) are exempt, as a
/// documented exemption in the design (decision 12 amendment): the derived-property handler
/// evaluates user getters from its attach callback, and derived getters with structural side
/// effects are a supported shape. Because of that exemption, and because this guard does not
/// exist in Release at all, the reconciler's released-parent early exits and the inexact
/// incoming-edge fallback in <see cref="SubjectOwnership.RemoveIncoming"/> remain load-bearing:
/// they are what keeps a reentrant callback descent correct rather than corrupting the graph.
/// Scalar writes from callbacks stay allowed.
/// </remarks>
internal static class CallbackReentrancyGuard
{
    [ThreadStatic]
    private static int _callbackDepth;

    /// <summary>Marks the thread as executing a lifecycle callback. Pair with
    /// <see cref="ExitCallback"/> in a finally block: callbacks are exception-free by contract,
    /// but a violating callback must not poison the thread's guard state for later operations.</summary>
    [Conditional("DEBUG")]
    public static void EnterCallback()
    {
        _callbackDepth++;
    }

    [Conditional("DEBUG")]
    public static void ExitCallback()
    {
        _callbackDepth--;
    }

    /// <summary>Called on entry of the lifecycle's structural write protocol.</summary>
    [Conditional("DEBUG")]
    public static void ThrowIfInsideCallback()
    {
        if (_callbackDepth > 0)
        {
            throw new InvalidOperationException(
                "A lifecycle callback must not write a structural (subject-typed) property. The " +
                "callback runs while the lifecycle holds its topology gate mid-reconcile, so the " +
                "write would re-enter the reconciler on half-updated edge state. Defer the write " +
                "until the triggering operation completes. This check only exists in Debug builds.");
        }
    }
}

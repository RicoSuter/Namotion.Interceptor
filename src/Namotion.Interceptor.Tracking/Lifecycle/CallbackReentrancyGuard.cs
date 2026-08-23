namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Enforces the callback contract: a lifecycle callback (an <see cref="ILifecycleHandler"/>
/// invocation, a subject attach or detach event, or a collection refresh) must not write a
/// structural property. The lifecycle publishes callbacks while it holds the topology gate in the
/// middle of reconciling an edge set, so a structural write from a callback re-enters the
/// reconciler on half-updated state. The depth is thread-local and shared across built-in
/// lifecycle instances, so a callback writing into another context's graph is detected too. The
/// guard is live in every build: the silent failure mode is graph corruption, so a violating
/// consumer fails fast in Release rather than relying on the reentrancy accommodations.
/// </summary>
/// <remarks>
/// The attach and detach property lifecycle callbacks
/// (<see cref="IPropertyLifecycleHandler.AttachProperty"/> and
/// <see cref="IPropertyLifecycleHandler.DetachProperty"/>) are exempt, deliberately: the
/// derived-property handler evaluates user getters from its attach callback, and derived getters
/// with structural side effects are a supported shape. Because of that exemption, the
/// reconciler's released-parent early exits and the inexact incoming-edge fallback in
/// <see cref="SubjectOwnership.RemoveIncoming"/> remain load-bearing. Scalar writes from
/// callbacks stay allowed.
/// </remarks>
internal static class CallbackReentrancyGuard
{
    [ThreadStatic]
    private static int _callbackDepth;

    /// <summary>
    /// Marks the thread as executing a lifecycle callback for the lifetime of the returned scope.
    /// Dispose the scope in a using statement, so the pairing lives here rather than in every
    /// publication site; callbacks are exception-free by contract, but a violating callback must
    /// not poison the thread's guard state for later operations.
    /// </summary>
    public static CallbackScope EnterScope()
    {
        _callbackDepth++;
        return default;
    }

    /// <summary>Called on entry of the lifecycle's structural write protocol.</summary>
    public static void ThrowIfInsideCallback()
    {
        if (_callbackDepth > 0)
        {
            throw new InvalidOperationException(
                "A lifecycle callback must not write a structural (subject-typed) property. The " +
                "callback runs while the lifecycle holds its topology gate mid-reconcile, so the " +
                "write would re-enter the reconciler on half-updated edge state. Defer the write " +
                "until the triggering operation completes.");
        }
    }

    internal readonly struct CallbackScope : IDisposable
    {
        public void Dispose()
        {
            _callbackDepth--;
        }
    }
}

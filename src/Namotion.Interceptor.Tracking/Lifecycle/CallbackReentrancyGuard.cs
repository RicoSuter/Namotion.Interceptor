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
/// AddProperties admission reads both depths through <see cref="IsInsideAnyCallback"/> to reject
/// a cross-context callback before it enumerates input or blocks on the foreign topology gate,
/// where waiting could deadlock against opposing callbacks.
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

    // Property callbacks are not exempt: a callback may evaluate anything and may mutate no
    // topology, so this depth feeds the contract check exactly like the lifecycle callback depth.
    [ThreadStatic]
    private static int _propertyCallbackDepth;

    /// <summary>
    /// Whether the current thread is executing any lifecycle callback of some built-in lifecycle,
    /// including the property lifecycle callbacks that are exempt from the structural write
    /// contract. Which lifecycle is answered by whether the thread holds that lifecycle's gate,
    /// because callbacks are always published under it.
    /// </summary>
    public static bool IsInsideAnyCallback => _callbackDepth > 0 || _propertyCallbackDepth > 0;

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
        if (IsInsideAnyCallback)
        {
            throw new LifecycleContractViolationException(
                "A lifecycle callback must not write a structural (subject-typed) property. The " +
                "callback runs while the lifecycle holds its topology gate mid-reconcile, so the " +
                "write would re-enter the reconciler on half-updated edge state. Defer the write " +
                "until the triggering operation completes.");
        }
    }

    /// <summary>
    /// Marks the thread as executing a property lifecycle callback for the lifetime of the
    /// returned scope. Property callbacks are not exempt: this depth feeds
    /// <see cref="ThrowIfInsideCallback"/> exactly like the lifecycle callback depth.
    /// </summary>
    public static PropertyCallbackScope EnterPropertyCallbackScope()
    {
        _propertyCallbackDepth++;
        return default;
    }

    internal readonly struct CallbackScope : IDisposable
    {
        public void Dispose()
        {
            _callbackDepth--;
        }
    }

    internal readonly struct PropertyCallbackScope : IDisposable
    {
        public void Dispose()
        {
            _propertyCallbackDepth--;
        }
    }
}

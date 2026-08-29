namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Enforces the callback contract: a lifecycle callback (an <see cref="ILifecycleHandler"/>
/// invocation, a subject attach or detach event, a collection refresh, or a property lifecycle
/// callback) may evaluate anything and may change no graph topology: no structural property
/// write, and no explicit attach or detach. The lifecycle publishes callbacks while it holds the
/// topology gate in the middle of reconciling an edge set, so a topology change from a callback
/// would re-enter the reconciler on half-updated state, and reaching a second lifecycle's gate
/// from inside a callback can deadlock because there is no order among gates. The depth is
/// thread-local and shared across built-in lifecycle instances, so a callback writing into
/// another context's graph is detected too. The guard is live in every build: the silent failure
/// mode is graph corruption, so a violating consumer fails fast in Release. Reaching a second
/// context's topology gate is rejected separately, by the one-transaction-per-thread rule in
/// <see cref="LifecycleInterceptor"/>, which binds every caller rather than only callbacks.
/// </summary>
/// <remarks>
/// The rule is uniform at every graph depth: the attach and detach property lifecycle callbacks
/// (<see cref="IPropertyLifecycleHandler.AttachProperty"/> and
/// <see cref="IPropertyLifecycleHandler.DetachProperty"/>) are not exempt. The derived-property
/// handler evaluates user getters from its attach callback, and evaluation is what the contract
/// permits. Scalar writes from callbacks stay allowed. The guard does not bind code running at
/// callback depth zero downstream of the lifecycle, such as a third-party write interceptor
/// during <c>next</c>; the ownership check at <see cref="StructuralReconciler"/> entry and the
/// released-parent exits inside its loops handle that shape.
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
    /// property lifecycle callbacks included, since they are not exempt from the contract. Which
    /// lifecycle is answered by whether the thread holds that lifecycle's gate, because callbacks
    /// are always published under it.
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

    /// <summary>Called on entry of every topology mutation: the structural write protocol, an
    /// explicit attach and an explicit detach.</summary>
    public static void ThrowIfInsideCallback()
    {
        if (IsInsideAnyCallback)
        {
            throw new LifecycleContractViolationException(
                "A lifecycle callback must not change graph topology: no structural " +
                "(subject-typed) property write, and no explicit attach or detach. The callback " +
                "runs while the lifecycle holds its topology gate mid-reconcile, so the change " +
                "would re-enter the reconciler on half-updated edge state, and reaching a second " +
                "lifecycle's gate from inside a callback can deadlock. Defer the change until the " +
                "triggering operation completes.");
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

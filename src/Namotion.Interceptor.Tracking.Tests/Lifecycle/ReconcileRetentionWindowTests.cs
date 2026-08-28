using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Reconciliation runs a complete removal pass before its addition pass. A subject the new value
/// still holds, but under a different key, therefore loses its only support in the removal pass and
/// is handed back to no context until the addition pass re-claims it.
/// </summary>
public class ReconcileRetentionWindowTests
{
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    /// <summary>
    /// Reproduces the finding that a retained subject becomes claimable during a transition. The
    /// window between the removal pass releasing the retained subject and the addition pass
    /// re-claiming it is held open artificially, by parking inside the detach callback of a second
    /// subject that the same removal pass releases afterwards. The window itself is real, and the
    /// callback is ordinary user code running inside it; only its width is manufactured.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAKeyMoveReleasesTheRetainedSubjectFirst_ThenAnotherContextCannotClaimIt()
    {
        // Arrange: the removal pass walks the old occurrences in reverse, so the moving car is
        // released first and the parking car's detach callback then runs while the moving car is
        // attached to nothing.
        var context = CreateContext();
        var otherContext = CreateContext();

        var released = new ManualResetEventSlim(false);
        var claimAttempted = new ManualResetEventSlim(false);
        var parkObserved = false;
        Car? mover = null;
        var handler = new DelegateLifecycleHandler(change =>
        {
            if (!change.IsContextDetach || change.Subject is not Car { Name: "parking" })
            {
                return;
            }

            released.Set();
            parkObserved = claimAttempted.Wait(RendezvousTimeout);
        });

        context.WithService(() => handler, _ => false);

        var garage = new Garage(context) { Name = "G" };
        mover = new Car { Name = "moving" };
        var parker = new Car { Name = "parking" };
        garage.CarsByName = new Dictionary<string, Car> { ["parking"] = parker, ["moving"] = mover };

        var moverWasUnattachedDuringRelease = false;
        Exception? claimException = null;
        var claimer = new Thread(() =>
        {
            if (!released.Wait(RendezvousTimeout))
            {
                return;
            }

            moverWasUnattachedDuringRelease = ((IInterceptorSubject)mover).TryGetContext() is null;
            claimException = Record.Exception(() => ((IInterceptorSubject)mover).AttachToContext(otherContext));
            claimAttempted.Set();
        })
        {
            IsBackground = true
        };

        // Act: the moving car stays in the new value, under a different key.
        claimer.Start();
        var assignmentException = Record.Exception(
            () => garage.CarsByName = new Dictionary<string, Car> { ["moved"] = mover });
        var claimerCompleted = claimer.Join(RendezvousTimeout);

        // Assert: the race actually happened, so the repro cannot pass without it.
        Assert.True(claimerCompleted, "the claiming thread never finished");
        Assert.True(parkObserved, "the release descent never parked inside the reconcile window");
        Assert.True(moverWasUnattachedDuringRelease,
            "the retained subject was expected to be unattached inside the reconcile window");
        Assert.Null(claimException);

        // A reachable subject must never become claimable during a transition, so the assignment
        // must not be defeated by another context winning the window.
        Assert.True(assignmentException is null,
            "the retained subject was released before the incoming occurrence secured it, so " +
            "another context claimed it mid-transition and the assignment failed with " +
            $"{assignmentException?.GetType().Name}: {assignmentException?.Message}");
        Assert.Same(context, ((IInterceptorSubject)mover).TryGetContext());
    }
}

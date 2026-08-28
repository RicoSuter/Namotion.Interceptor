using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A structural write enters the target context's topology gate before the write chain is resolved,
/// so the callback guard that would reject a cross-context write from inside a lifecycle callback
/// only runs once that gate is already held. Two symmetric callbacks therefore acquire two gates in
/// opposite order. The explicit attach, detach and property admission entry points all check before
/// their gate; the structural write is the one topology mutation that does not.
/// </summary>
public class CrossContextGateDeadlockTests
{
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    /// <summary>
    /// Reproduces the finding that a cross-context structural write from a lifecycle callback takes
    /// the foreign gate before any guard runs. The rendezvous is artificial, because it lines both
    /// callbacks up inside their own gates at the same time; the acquisition order they then take
    /// is the production one. A deadlock shows up as a bounded join failing rather than as a hung
    /// suite, and the worker threads are background threads so a deadlocked pair cannot keep the
    /// run alive.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTwoAttachCallbacksCrossWriteIntoEachOthersContexts_ThenBothOperationsComplete()
    {
        // Arrange: each context holds a subject the other context's attach callback writes to.
        var rendezvous = new Barrier(2);
        var rendezvousReached = 0;
        Person? pinnedInFirst = null;
        Person? pinnedInSecond = null;
        Exception? firstCrossWriteException = null;
        Exception? secondCrossWriteException = null;

        var firstHandler = new DelegateLifecycleHandler(change =>
        {
            if (!change.IsContextAttach || change.Subject is not Person { FirstName: "trigger" } || pinnedInSecond is null)
            {
                return;
            }

            if (!rendezvous.SignalAndWait(RendezvousTimeout))
            {
                return;
            }

            Interlocked.Increment(ref rendezvousReached);
            firstCrossWriteException = Record.Exception(
                () => pinnedInSecond.Father = new Person { FirstName = "fromFirst" });
        });

        var secondHandler = new DelegateLifecycleHandler(change =>
        {
            if (!change.IsContextAttach || change.Subject is not Person { FirstName: "trigger" } || pinnedInFirst is null)
            {
                return;
            }

            if (!rendezvous.SignalAndWait(RendezvousTimeout))
            {
                return;
            }

            Interlocked.Increment(ref rendezvousReached);
            secondCrossWriteException = Record.Exception(
                () => pinnedInFirst.Father = new Person { FirstName = "fromSecond" });
        });

        var firstContext = CreateContext().WithService(() => firstHandler, _ => false);
        var secondContext = CreateContext().WithService(() => secondHandler, _ => false);

        pinnedInFirst = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInFirst).AttachToContext(firstContext);
        pinnedInSecond = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInSecond).AttachToContext(secondContext);

        var firstTrigger = new Person { FirstName = "trigger" };
        var secondTrigger = new Person { FirstName = "trigger" };

        var firstAttach = new Thread(
            () => ((IInterceptorSubject)firstTrigger).AttachToContext(firstContext)) { IsBackground = true };
        var secondAttach = new Thread(
            () => ((IInterceptorSubject)secondTrigger).AttachToContext(secondContext)) { IsBackground = true };

        // Act
        firstAttach.Start();
        secondAttach.Start();
        var firstCompleted = firstAttach.Join(JoinTimeout);
        var secondCompleted = firstCompleted && secondAttach.Join(JoinTimeout);

        // Assert: both callbacks reached the cross write, so a timeout cannot pass by serializing.
        Assert.Equal(2, Volatile.Read(ref rendezvousReached));

        // Both operations must terminate. A cross-context write from a callback may legitimately be
        // rejected, but it must be rejected rather than block on the other context's gate.
        Assert.True(firstCompleted && secondCompleted,
            "probable ABBA deadlock on two lifecycle gates: the first attach " +
            $"{(firstCompleted ? "completed" : "did not complete")} and the second attach " +
            $"{(secondCompleted ? "completed" : "did not complete")} within {JoinTimeout.TotalSeconds:F0} seconds");
        Assert.IsType<LifecycleContractViolationException>(firstCrossWriteException);
        Assert.IsType<LifecycleContractViolationException>(secondCrossWriteException);
    }
}

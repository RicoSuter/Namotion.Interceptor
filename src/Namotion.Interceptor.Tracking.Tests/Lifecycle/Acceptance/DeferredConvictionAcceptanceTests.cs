using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle.Acceptance;

/// <summary>
/// Defect class 11, deferred rather than closed: the best-effort half of the withheld verdict. A
/// conviction reached while draining a withheld recalculation is traced rather than thrown, because
/// the drain runs from a thread that was only ending an unrelated transaction, and failing that
/// thread's operation for a bug in someone else's derived getter is worse than reporting it. The
/// verdict is then raised against a caller on the next evaluation, if one occurs. Nothing schedules
/// that evaluation, so the second half is best effort and not a guarantee.
/// </summary>
public class DeferredConvictionAcceptanceTests
{
    private static ScalarTriggeredOrphanSubject ArmOrphan(IInterceptorSubjectContext context)
    {
        var subject = new ScalarTriggeredOrphanSubject();
        ((IInterceptorSubject)subject).AttachToContext(context);
        subject.Orphan = new Person { FirstName = "orphan" };
        return subject;
    }

    /// <summary>
    /// PASSES on this branch. The half that must hold whatever the design does with the verdict: a
    /// thread that was only ending an unrelated transaction is never failed by someone else's
    /// derived getter. It passes here for a different reason than the one it was written for, since
    /// this branch convicts the triggering thread outright and so has nothing to drain, which is
    /// what the second test records.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAnOrphanIsJudgedWhileAnUnrelatedAttachIsParked_ThenTheParkedThreadStillCompletes()
    {
        // Arrange
        var context = AcceptanceContext.CreateWithDerived();
        var subject = ArmOrphan(context);

        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var unrelated = new EnumerableChildrenHolder
        {
            Children = new ParkingEnumerable(() =>
            {
                parked.Set();
                release.Wait(WriteProtocolAcceptance.RendezvousTimeout);
            })
        };

        Exception? attachException = null;
        var attaching = new Thread(
            () => attachException = Record.Exception(() => ((IInterceptorSubject)unrelated).AttachToContext(context)))
        {
            IsBackground = true
        };
        attaching.Start();
        var reachedPark = parked.Wait(WriteProtocolAcceptance.RendezvousTimeout);

        // Act
        Record.Exception(() => subject.Name = "trigger");
        release.Set();
        var attachCompleted = attaching.Join(WriteProtocolAcceptance.JoinTimeout);

        // Assert
        Assert.True(reachedPark, "the unrelated attach never parked inside the topology gate");
        Assert.True(attachCompleted, "the parked attach never finished");
        Assert.Null(attachException);
        Assert.Same(context, ((IInterceptorSubject)unrelated).TryGetContext());
    }

    /// <summary>
    /// FAILS on this branch. Demonstrates that withholding is not dropping: a verdict withheld
    /// because a transaction was in flight has to survive that transaction and be raised at the next
    /// evaluation, so one undrained deferral never becomes a permanent acquittal for that property.
    /// Observed symptom: the guard fails first. The triggering write throws
    /// LifecycleContractViolationException while the unrelated attach is still parked, so no verdict
    /// is ever withheld and there is nothing for this test to prove was not dropped. The withholding
    /// phase this instrument depends on does not exist on this branch.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAVerdictWasWithheldOnce_ThenItIsStillRaisedAtTheNextEvaluation()
    {
        // Arrange
        var context = AcceptanceContext.CreateWithDerived();
        var subject = ArmOrphan(context);

        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var unrelated = new EnumerableChildrenHolder
        {
            Children = new ParkingEnumerable(() =>
            {
                parked.Set();
                release.Wait(WriteProtocolAcceptance.RendezvousTimeout);
            })
        };

        var attaching = new Thread(() => ((IInterceptorSubject)unrelated).AttachToContext(context)) { IsBackground = true };
        attaching.Start();
        Assert.True(parked.Wait(WriteProtocolAcceptance.RendezvousTimeout),
            "the unrelated attach never parked inside the topology gate");

        // Act
        var withheldTrigger = Record.Exception(() => subject.Name = "trigger");
        release.Set();
        var attachCompleted = attaching.Join(WriteProtocolAcceptance.JoinTimeout);
        var laterTrigger = Record.Exception(() => subject.Name = "again");

        // Assert
        Assert.True(attachCompleted, "the parked attach never finished");
        Assert.Null(withheldTrigger);
        Assert.IsType<LifecycleContractViolationException>(laterTrigger);
    }
}

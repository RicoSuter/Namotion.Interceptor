using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle.Acceptance;

/// <summary>
/// Defect class 5: a derived value that keeps exposing a subject this context does not own is
/// convicted at the retry bound, unless a topology transaction is in flight that could still be
/// publishing it. The verdict is then withheld rather than dropped, so a value that was merely
/// mid-publication converges and a genuine orphan comes back for judgement. These pin that
/// withholding a verdict never becomes losing it, and that convicting is never done on first sight.
/// </summary>
public class MidPublicationVerdictAcceptanceTests
{
    private static ScalarTriggeredOrphanSubject ArmOrphan(IInterceptorSubjectContext context)
    {
        var subject = new ScalarTriggeredOrphanSubject();
        ((IInterceptorSubject)subject).AttachToContext(context);
        subject.Orphan = new Person { FirstName = "orphan" };
        return subject;
    }

    /// <summary>
    /// FAILS on this branch. Demonstrates defect 5's first half: the conviction must come out of the
    /// bounded retry loop rather than the first detection, because a subject seen unattached once
    /// may simply be mid-publication. Observed symptom: the correct exception type is raised, but
    /// after exactly one evaluation, so nothing was retried and no chance to converge was given.
    /// This branch convicts on first sight.
    /// </summary>
    [Fact]
    public void WhenNoTransactionIsInFlight_ThenAGenuineOrphanIsConvicted()
    {
        // Arrange
        var context = AcceptanceContext.CreateWithDerived();
        var subject = ArmOrphan(context);
        var evaluationsBefore = subject.EvaluationCount;

        // Act
        var exception = Record.Exception(() => subject.Name = "trigger");

        // Assert
        Assert.IsType<LifecycleContractViolationException>(exception);
        Assert.True(subject.EvaluationCount - evaluationsBefore >= DerivedPropertyChangeHandler.MaxStabilizationIterations,
            "the conviction must come out of the bounded retry loop, not the first detection; " +
            $"observed {subject.EvaluationCount - evaluationsBefore} evaluation(s)");
    }

    /// <summary>
    /// FAILS on this branch. Demonstrates defect 5's second half: a transaction in flight while a
    /// genuine orphan is evaluated buys that orphan a deferral and nothing more. The write that ran
    /// into it is innocent and must complete. Observed symptom: the innocent trigger throws
    /// LifecycleContractViolationException immediately while an unrelated attach is parked inside
    /// the topology gate, so the verdict is not withheld at all and the caller is failed for a race
    /// it could not have avoided. Held open by parking inside the discovery scan, which is user code
    /// running under the gate, so the gate really is held for the whole recalculation.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAnUnrelatedAttachHoldsTheGate_ThenTheOrphanIsJudgedOnceItEnds()
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
        var reachedPark = parked.Wait(WriteProtocolAcceptance.RendezvousTimeout);

        // Act
        var exception = Record.Exception(() => subject.Name = "trigger");
        var evaluationsWhenWithheld = subject.EvaluationCount;

        release.Set();
        var attachCompleted = attaching.Join(WriteProtocolAcceptance.JoinTimeout);

        // Assert
        Assert.True(reachedPark, "the unrelated attach never parked inside the topology gate");
        Assert.True(attachCompleted, "the parked attach never finished");
        Assert.Null(exception);

        Assert.True(subject.EvaluationCount > evaluationsWhenWithheld,
            "the withheld value was never re-evaluated when the transaction it was waiting on ended");

        Assert.IsType<LifecycleContractViolationException>(
            Record.Exception(() => subject.Name = "again"));
    }

    /// <summary>
    /// FAILS on this branch. The same deferral against a structural write rather than an attach, so
    /// the pin does not depend on one entry point into the gate. Observed symptom: identical to the
    /// attach variant, the innocent trigger throws LifecycleContractViolationException while the
    /// unrelated write is parked inside the gate.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAnUnrelatedStructuralWriteHoldsTheGate_ThenTheOrphanIsJudgedOnceItEnds()
    {
        // Arrange
        var context = AcceptanceContext.CreateWithDerived();
        var subject = ArmOrphan(context);
        var unrelated = new EnumerableChildrenHolder();
        ((IInterceptorSubject)unrelated).AttachToContext(context);

        var parked = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var writing = new Thread(() => unrelated.Children = new ParkingEnumerable(() =>
        {
            parked.Set();
            release.Wait(WriteProtocolAcceptance.RendezvousTimeout);
        }))
        {
            IsBackground = true
        };

        writing.Start();
        var reachedPark = parked.Wait(WriteProtocolAcceptance.RendezvousTimeout);

        // Act
        var exception = Record.Exception(() => subject.Name = "trigger");
        var evaluationsWhenWithheld = subject.EvaluationCount;

        release.Set();
        var writeCompleted = writing.Join(WriteProtocolAcceptance.JoinTimeout);

        // Assert
        Assert.True(reachedPark, "the unrelated write never parked inside the topology gate");
        Assert.True(writeCompleted, "the parked write never finished");
        Assert.Null(exception);

        Assert.True(subject.EvaluationCount > evaluationsWhenWithheld,
            "the withheld value was never re-evaluated when the transaction it was waiting on ended");

        Assert.IsType<LifecycleContractViolationException>(
            Record.Exception(() => subject.Name = "again"));
    }
}

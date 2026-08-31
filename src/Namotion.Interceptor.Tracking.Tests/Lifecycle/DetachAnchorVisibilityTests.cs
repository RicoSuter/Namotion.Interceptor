using System.Reflection;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// What a lifecycle handler sees on the anchor of a subject that is being released, which is the
/// invariant <see cref="Namotion.Interceptor.Tracking.Parent.ParentsHandlerExtensions.GetParents"/> documents to consumers deciding root-ness
/// from inside a callback. Nothing else asserts the anchor from within a callback: the ownership
/// oracle compares post-state only.
/// </summary>
public class DetachAnchorVisibilityTests
{
    [Fact]
    public void WhenAReleasedChildIsObservedFromItsDetachCallback_ThenItCarriesNoAnchor()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };
        simulation.Component = component;

        SubjectAttachmentAnchorKind? anchorDuringDetach = null;
        IInterceptorSubjectContext? contextDuringDetach = null;
        AttachmentPhase? phaseDuringDetach = null;
        context.AddService(new DelegateLifecycleHandler(change =>
        {
            if (ReferenceEquals(change.Subject, component) && !change.IsContextAttach)
            {
                anchorDuringDetach = change.Subject.Executor.AttachmentAnchor;
                contextDuringDetach = change.Subject.TryGetContext();
                phaseDuringDetach = ((InterceptorExecutor)change.Subject.Executor).CurrentAttachmentPhase;
            }
        }));

        // Act
        simulation.Component = null;

        // Assert: ownership is dropped before any detach callback runs, but the anchor is what
        // decides root-ness, and an edge-held child never had one. The same detaching record and
        // final-clear protocol serves structural releases and explicit anchor removal.
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorDuringDetach);
        Assert.Same(context, contextDuringDetach);
        Assert.Equal(AttachmentPhase.Detaching, phaseDuringDetach);
        Assert.Null(((IInterceptorSubject)component).TryGetContext());
        Assert.Equal(AttachmentPhase.Stable,
            ((InterceptorExecutor)((IInterceptorSubject)component).Executor).CurrentAttachmentPhase);
    }

    [Fact]
    public void WhenAnExplicitRootIsObservedFromItsDetachCallback_ThenItCarriesNoAnchor()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation { Name = "Root" };
        simulation.AttachToContext(context);

        SubjectAttachmentAnchorKind? anchorDuringDetach = null;
        IInterceptorSubjectContext? contextDuringDetach = null;
        AttachmentPhase? phaseDuringDetach = null;
        context.AddService(new DelegateLifecycleHandler(change =>
        {
            if (ReferenceEquals(change.Subject, simulation) && !change.IsContextAttach)
            {
                anchorDuringDetach = change.Subject.Executor.AttachmentAnchor;
                contextDuringDetach = change.Subject.TryGetContext();
                phaseDuringDetach = ((InterceptorExecutor)change.Subject.Executor).CurrentAttachmentPhase;
            }
        }));

        // Act
        simulation.DetachFromContext(context);

        // Assert: the detach clears the anchor before releasing, so a handler never sees a departing
        // root reported as one. Its exact context remains available in the detaching record until
        // every callback has drained, then the final publication clears it.
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorDuringDetach);
        Assert.Same(context, contextDuringDetach);
        Assert.Equal(AttachmentPhase.Detaching, phaseDuringDetach);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)simulation).Executor.AttachmentAnchor);
        Assert.Null(((IInterceptorSubject)simulation).TryGetContext());
        Assert.Equal(AttachmentPhase.Stable,
            ((InterceptorExecutor)((IInterceptorSubject)simulation).Executor).CurrentAttachmentPhase);
    }

    [Fact]
    public void WhenAStaleDetachmentFinalizerFindsNewSupport_ThenItDoesNotClearTheAttachment()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation(context) { Name = "Root" };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var executor = (InterceptorExecutor)((IInterceptorSubject)simulation).Executor;
        executor.TryGetAttachment(out _, out _, out var revision);
        var staleDetachment = new OwnershipGraph.DetachmentPlan(
            executor, (InterceptorSubjectContext)context, revision);

        // Act
        var exception = Record.Exception(() => lifecycle.CompleteDetachments([staleDetachment]));

        // Assert: a delayed finalizer is conditional on graph absence. New ownership wins without
        // throwing from the cleanup path or clearing the subject's newer supported attachment.
        Assert.Null(exception);
        Assert.Same(context, simulation.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional,
            ((IInterceptorSubject)simulation).Executor.AttachmentAnchor);
        Assert.Equal(AttachmentPhase.Stable, executor.CurrentAttachmentPhase);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task WhenFinalClearWaitsForAttachmentLock_ThenItIsNotATopologyTransaction()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation(context) { Name = "Root" };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var executor = (InterceptorExecutor)((IInterceptorSubject)simulation).Executor;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var attachmentLock = typeof(InterceptorExecutor).GetField("_attachmentLock", flags)!.GetValue(executor)!;
        var callbackEntered = new ManualResetEventSlim(false);
        var callbackReturning = new ManualResetEventSlim(false);
        var attachmentLockHeld = new ManualResetEventSlim(false);
        var releaseAttachmentLock = new ManualResetEventSlim(false);
        var allowCallbackToReturn = new ManualResetEventSlim(false);
        lifecycle.SubjectDetaching += change =>
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, simulation))
            {
                callbackEntered.Set();
                if (!allowCallbackToReturn.Wait(WriteProtocolAcceptance.RendezvousTimeout))
                {
                    throw new TimeoutException("Timed out waiting to return from the detach callback.");
                }

                callbackReturning.Set();
            }
        };

        var lockHolder = new Thread(() =>
        {
            if (!callbackEntered.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                return;
            }

            lock (attachmentLock)
            {
                attachmentLockHeld.Set();
                releaseAttachmentLock.Wait(WriteProtocolAcceptance.RendezvousTimeout);
            }
        }) { IsBackground = true };
        Exception? detachException = null;
        var detacher = new Thread(() =>
        {
            detachException = Record.Exception(() => simulation.DetachFromContext(context));
        }) { IsBackground = true };

        // Act
        lockHolder.Start();
        detacher.Start();
        Assert.True(callbackEntered.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        Assert.True(attachmentLockHeld.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        allowCallbackToReturn.Set();
        Assert.True(callbackReturning.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        var recalculationRan = false;
        var withheld = false;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => (detacher.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                message: "the final clear did not wait for the attachment lock");
            withheld = lifecycle.TryRunWhenTransactionEnds(() => recalculationRan = true);
        }
        finally
        {
            releaseAttachmentLock.Set();
        }

        // Assert
        Assert.True(detacher.Join(WriteProtocolAcceptance.RendezvousTimeout));
        Assert.True(lockHolder.Join(WriteProtocolAcceptance.RendezvousTimeout));
        Assert.Null(detachException);
        Assert.False(withheld);
        Assert.False(recalculationRan);
        Assert.Null(simulation.TryGetContext());
        Assert.Equal(AttachmentPhase.Stable, executor.CurrentAttachmentPhase);
    }
}

using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class SubjectAttachmentTests
{
    private sealed class LifecycleProbe : ILifecycleInterceptor
    {
        public int AttachCount;
        public int DetachCount;

        private readonly object _structuralWriteGate = new();

        public void EnterStructuralWriteGate() => Monitor.Enter(_structuralWriteGate);

        public void ExitStructuralWriteGate() => Monitor.Exit(_structuralWriteGate);

        // No ownership work in the probe: publishing the metadata is the whole admission.
        public bool TryAddProperties(SubjectPropertyRegistrationContext registration)
        {
            registration.Publish();
            return true;
        }

        // A minimal faithful lifecycle: it applies the documented root-anchor rules through Core's
        // own helpers and counts each policy entry, which is where a real implementation does its
        // graph work.
        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAnchorKind anchor)
        {
            InterceptorSubjectExtensions.ApplyRootAnchor(subject, context, anchor);
            AttachCount++;
        }

        // The probe tracks no structural edges, so its detach policy is the minimal one: clear the
        // anchor, keep the attachment.
        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out var attachedContext, out var anchor, out var revision);
            InterceptorSubjectExtensions.ValidateExplicitDetach(attachedContext, anchor, context);
            executor.TryUpdateAttachment(revision, attachedContext, SubjectAnchorKind.None, out _);
            DetachCount++;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
        }
    }

    private static IInterceptorExecutor GetExecutor(IInterceptorSubject subject)
    {
        return subject.Executor;
    }

    private static InterceptorSubjectContext CreateContextWithProbe(out LifecycleProbe probe)
    {
        var context = InterceptorSubjectContext.Create();
        probe = new LifecycleProbe();
        context.AddService(probe);
        return context;
    }

    [Fact]
    public void WhenSubjectIsUnattached_ThenTryGetContextReturnsNull()
    {
        // Arrange
        var subject = new Car();

        // Act
        var context = subject.TryGetContext();

        // Assert
        Assert.Null(context);
    }

    [Fact]
    public void WhenSubjectIsUnattached_ThenGetContextThrows()
    {
        // Arrange
        var subject = new Car();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.GetContext());
    }

    [Fact]
    public void WhenAttachingUnattachedSubject_ThenSubjectIsExplicitlyAttachedAndLifecycleRuns()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);

        // Act
        subject.AttachToContext(context);

        // Assert
        Assert.Same(context, subject.TryGetContext());
        Assert.Same(context, subject.GetContext());
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.Explicit, executor.Anchor);
        Assert.Equal(1, probe.AttachCount);
    }

    [Fact]
    public void WhenAttachingSubjectAttachedWithNoneAnchorToSameContext_ThenAnchorIsPromotedToExplicit()
    {
        // Arrange
        var context = CreateContextWithProbe(out _);
        var subject = new Car();
        var executor = GetExecutor(subject);
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, context, SubjectAnchorKind.None, out _));

        // Act
        subject.AttachToContext(context);

        // Assert
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.Explicit, executor.Anchor);
    }

    [Fact]
    public void WhenAttachingSubjectAttachedWithProvisionalAnchorToSameContext_ThenAnchorIsPromotedToExplicit()
    {
        // Arrange
        var context = CreateContextWithProbe(out _);
        var subject = new Car();
        var executor = GetExecutor(subject);
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, context, SubjectAnchorKind.Provisional, out _));

        // Act
        subject.AttachToContext(context);

        // Assert
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.Explicit, executor.Anchor);
    }

    [Fact]
    public void WhenAttachingAlreadyExplicitlyAttachedSubjectToSameContext_ThenThrowsBeforeAnyStateChange()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        subject.AttachToContext(context);
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.AttachToContext(context));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.Explicit, executor.Anchor);
        Assert.Equal(1, probe.AttachCount);
    }

    [Fact]
    public void WhenAttachingSubjectAttachedToDifferentContext_ThenThrowsBeforeAnyStateChange()
    {
        // Arrange
        var firstContext = CreateContextWithProbe(out var firstProbe);
        var secondContext = CreateContextWithProbe(out var secondProbe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        subject.AttachToContext(firstContext);
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.AttachToContext(secondContext));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Same(firstContext, executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.Explicit, executor.Anchor);
        Assert.Equal(1, firstProbe.AttachCount);
        Assert.Equal(0, secondProbe.AttachCount);
    }

    [Fact]
    public void WhenDetachingExplicitlyAttachedSubject_ThenAnchorClearsAndLifecycleDetaches()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        subject.AttachToContext(context);
        var revisionBefore = executor.AttachmentRevision;

        // Act
        subject.DetachFromContext(context);

        // Assert: only the anchor clears in this stage; the exact context is cleared once
        // structural edges become authoritative.
        Assert.Equal(SubjectAnchorKind.None, executor.Anchor);
        Assert.Same(context, executor.AttachedContext);
        Assert.True(executor.AttachmentRevision > revisionBefore);
        Assert.Equal(1, probe.DetachCount);
    }

    [Fact]
    public void WhenDetachingUnattachedSubject_ThenThrowsBeforeAnyStateChange()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.DetachFromContext(context));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Null(executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.None, executor.Anchor);
        Assert.Equal(0, probe.DetachCount);
    }

    [Fact]
    public void WhenDetachingSubjectWithProvisionalAnchor_ThenThrowsBeforeAnyStateChange()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, context, SubjectAnchorKind.Provisional, out _));
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.DetachFromContext(context));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.Provisional, executor.Anchor);
        Assert.Equal(0, probe.DetachCount);
    }

    [Fact]
    public void WhenDetachingExplicitlyAttachedSubjectFromDifferentContext_ThenThrowsBeforeAnyStateChange()
    {
        // Arrange
        var attachedContext = CreateContextWithProbe(out var probe);
        var otherContext = CreateContextWithProbe(out _);
        var subject = new Car();
        var executor = GetExecutor(subject);
        subject.AttachToContext(attachedContext);
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.DetachFromContext(otherContext));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Same(attachedContext, executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.Explicit, executor.Anchor);
        Assert.Equal(0, probe.DetachCount);
    }

    [Fact]
    public void WhenSubjectIsConstructedWithContext_ThenItIsProvisionallyAttached()
    {
        // Arrange & Act: the context-taking constructor routes through the context's lifecycle
        // with a provisional anchor.
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car(context);
        var executor = GetExecutor(subject);

        // Assert
        Assert.Equal(1, probe.AttachCount);
        Assert.Same(context, subject.TryGetContext());
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.Provisional, executor.Anchor);
    }

    [Fact]
    public void WhenAttachingSubjectConstructedWithContext_ThenAnchorIsPromotedToExplicit()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car(context);
        var executor = GetExecutor(subject);

        // Act
        subject.AttachToContext(context);

        // Assert: the explicit attach promotes the constructor's provisional anchor, entering the
        // lifecycle's policy a second time.
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.Explicit, executor.Anchor);
        Assert.Equal(2, probe.AttachCount);
    }

    [Fact]
    public void WhenReadingTheAttachedContext_ThenNothingIsAllocated()
    {
        // Arrange: the context read is the ownership predicate across the codebase, so it must
        // stay allocation-free for attached and unattached subjects alike.
        var unattached = new Car();
        var attached = new Car();
        attached.AttachToContext(InterceptorSubjectContext.Create());
        for (var i = 0; i < 100; i++)
        {
            unattached.TryGetContext();
            attached.TryGetContext();
        }

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            unattached.TryGetContext();
            attached.TryGetContext();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WhenTheAttachmentMonitorIsHeldByACommit_ThenAttachmentReadsStillComplete()
    {
        // Arrange: the structural terminal holds the attachment monitor through the commit, so a
        // backing write delegate that blocks keeps it held for as long as the test wants. The
        // attachment reads below only complete while it is held if they take no lock, which is
        // what lets consumers call them from inside their own locks without deadlocking.
        var subject = new Car();
        var executor = GetExecutor(subject);
        using var insideCommit = new ManualResetEventSlim(false);
        using var resumeCommit = new ManualResetEventSlim(false);
        var commitResumedInTime = false;

        var writer = new Thread(() => executor.SetStructuralPropertyValue<int>("Speed", 42, 0, (_, _) =>
        {
            insideCommit.Set();
            commitResumedInTime = resumeCommit.Wait(TimeSpan.FromSeconds(10));
        }));
        writer.Start();
        Assert.True(insideCommit.Wait(TimeSpan.FromSeconds(10)));

        // Act: read from inside a lock the caller already holds, like consumers do.
        IInterceptorSubjectContext? attachedContext;
        SubjectAnchorKind anchor;
        long revision;
        var callerLock = new object();
        lock (callerLock)
        {
            attachedContext = subject.TryGetContext();
            anchor = executor.Anchor;
            revision = executor.AttachmentRevision;
        }
        resumeCommit.Set();
        writer.Join();

        // Assert: the reads completed while the monitor was held (the commit was still waiting
        // when they finished) and observed the pre-commit state.
        Assert.True(commitResumedInTime);
        Assert.Null(attachedContext);
        Assert.Equal(SubjectAnchorKind.None, anchor);
        Assert.Equal(0, revision);
    }

    [Fact]
    public void WhenDetachingAndReattaching_ThenLifecycleRunsAgain()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);

        // Act
        subject.AttachToContext(context);
        subject.DetachFromContext(context);
        subject.AttachToContext(context);

        // Assert: the second attach promotes the leftover None anchor and re-adds the fallback.
        Assert.Equal(2, probe.AttachCount);
        Assert.Equal(1, probe.DetachCount);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAnchorKind.Explicit, executor.Anchor);
    }
}

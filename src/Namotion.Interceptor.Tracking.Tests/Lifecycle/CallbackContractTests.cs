using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackContractTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    [Fact]
    public void WhenAPropertyCallbackWritesStructuralPropertyAtTopLevel_ThenBothRootsSettle()
    {
        // Arrange
        // Construction also publishes callbacks before the local receives the constructed subject.
        Exception? callbackException = null;
        var attempted = false;
        Person? stranger = null;
        var handler = new DelegatePropertyAttachHandler(change =>
        {
            if (attempted || stranger is null)
            {
                return;
            }

            attempted = true;
            callbackException = Record.Exception(() => stranger.Father = new Person());
        });

        var context = CreateContext().WithRegistry().WithService(() => handler, _ => false);
        stranger = new Person(context) { FirstName = "S" };

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.True(attempted);
        Assert.Null(callbackException);
        var child = Assert.IsType<Person>(stranger.Father);
        SupportContractAssertions.Settled(context, [stranger, root], stranger, root, child);
        stranger.AttachToContext(context);
        root.AttachToContext(context);
        stranger.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [root], stranger, root, child);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], stranger, root, child);
    }

    [Fact]
    public void WhenAPropertyCallbackWritesStructuralPropertyBelowTheFirstLevel_ThenBothSubtreesSettle()
    {
        // Arrange
        Exception? deepException = null;
        var attempted = false;
        Person? stranger = null;
        var handler = new DelegatePropertyAttachHandler(change =>
        {
            if (attempted || stranger is null || change.Subject is not Person { FirstName: "leaf" })
            {
                return;
            }

            attempted = true;
            deepException = Record.Exception(() => stranger.Father = new Person());
        });

        var context = CreateContext().WithRegistry().WithService(() => handler, _ => false);
        stranger = new Person(context) { FirstName = "S" };

        var top = new Person(context) { FirstName = "top" };
        var mid = new Person { FirstName = "mid" };
        var leaf = new Person { FirstName = "leaf" };
        mid.Father = leaf;

        // Act
        top.Father = mid;

        // Assert
        Assert.True(attempted);
        Assert.Null(deepException);
        var child = Assert.IsType<Person>(stranger.Father);
        SupportContractAssertions.Settled(context, [stranger, top], stranger, top, mid, leaf, child);
        stranger.AttachToContext(context);
        top.AttachToContext(context);
        stranger.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [top], stranger, top, mid, leaf, child);
        top.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], stranger, top, mid, leaf, child);
    }

    [Fact]
    public void WhenALifecycleCallbackAttachesASubject_ThenBothRootsRemainIndependentlyDetachable()
    {
        // Arrange
        // Nested attachment queues further callbacks, so the handler runs only once.
        Exception? callbackException = null;
        var attempted = false;
        var introduced = new Person { FirstName = "X" };
        var context = CreateContext().WithRegistry()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (attempted)
                {
                    return;
                }

                attempted = true;
                callbackException = Record.Exception(
                    () => introduced.AttachToContext(change.Subject.GetContext()));
            }), _ => false);

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.True(attempted);
        Assert.Null(callbackException);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)introduced).Executor.AttachmentAnchor);
        SupportContractAssertions.Settled(context, [root, introduced], root, introduced);
        introduced.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [root], root, introduced);
        root.AttachToContext(context);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, introduced);
    }

    [Fact]
    public void WhenALifecycleCallbackDetachesASubject_ThenTheExplicitRootIsReleased()
    {
        // Arrange
        Exception? callbackException = null;
        Person? pinned = null;
        var attempted = false;
        var context = CreateContext().WithRegistry()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (attempted || pinned is null || ReferenceEquals(change.Subject, pinned))
                {
                    return;
                }

                attempted = true;
                callbackException = Record.Exception(() => pinned.DetachFromContext(pinned.GetContext()));
            }), _ => false);

        // Explicit attach, not a context constructor: a constructed subject carries a Provisional
        // anchor, and ValidateDetach already rejects detaching one with a plain
        // InvalidOperationException, so the test would pass pre-fix for the wrong reason.
        pinned = new Person { FirstName = "P" };
        pinned.AttachToContext(context);

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.True(attempted);
        Assert.Null(callbackException);
        SupportContractAssertions.Settled(context, [root], root, pinned);
        root.AttachToContext(context);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, pinned);
    }

    [Fact]
    public void WhenTwoLifecyclesAttachIntoEachOtherFromCallbacks_ThenNeitherDeadlocks()
    {
        // Arrange: the shape of a cross-lifecycle gate deadlock. Each thread holds its
        // own gate inside a callback and reaches for the other's. The contract must reject the
        // attach before either gate is requested, so both threads finish.
        var first = CreateContext();
        var second = CreateContext();
        var ready = new CountdownEvent(2);

        void Body(IInterceptorSubjectContext own, IInterceptorSubjectContext other)
        {
            own.WithService(() => new DelegateLifecycleHandler(_ =>
            {
                ready.Signal();
                ready.Wait(TimeSpan.FromSeconds(5));
                Record.Exception(() => new Person { FirstName = "X" }.AttachToContext(other));
            }), _ => false);

            _ = new Person(own) { FirstName = "R" };
        }

        // Act
        var a = new Thread(() => Body(first, second)) { IsBackground = true };
        var b = new Thread(() => Body(second, first)) { IsBackground = true };
        a.Start();
        b.Start();

        // Assert: a bounded join, so a regression fails the test instead of hanging the suite.
        Assert.True(a.Join(TimeSpan.FromSeconds(10)), "thread a did not finish, the gates deadlocked");
        Assert.True(b.Join(TimeSpan.FromSeconds(10)), "thread b did not finish, the gates deadlocked");
    }

    /// <summary>
    /// Derived getters are only evaluated when DerivedPropertyChangeHandler is registered, which
    /// WithLifecycle() alone does not do. Without this the tests below pass vacuously.
    /// </summary>
    private static IInterceptorSubjectContext CreateDerivedContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithDerivedPropertyChangeDetection();
    }

    [Fact]
    public void WhenADerivedPropertyExposesAnUnattachedSubject_ThenItRemainsAProjection()
    {
        // Arrange
        var context = CreateDerivedContext();

        // Act
        var subject = new LazyDerivedSubject(context);

        // Assert
        Assert.Same(context, subject.TryGetContext());
        Assert.Null(subject.Current.TryGetContext());
        Assert.Equal(0, subject.Current.GetReferenceCount());
    }

    [Fact]
    public void WhenTheAttachEvaluationReturnsAProjection_ThenItsValueIsRecordedWithoutOwnership()
    {
        // Arrange
        var context = CreateDerivedContext();
        var subject = new LazyDerivedSubject();

        // Act
        subject.AttachToContext(context);

        // Assert
        var data = new PropertyReference(subject, nameof(LazyDerivedSubject.Current)).TryGetDerivedPropertyData();
        Assert.NotNull(data);
        Assert.Same(subject.Current, data.LastKnownValue);
        Assert.Null(subject.Current.TryGetContext());
    }

    [Fact]
    public void WhenADerivedPropertyProjectsAnAttachedSubject_ThenItDoesNotThrow()
    {
        // Arrange
        var context = CreateDerivedContext();

        // Act: FirstChild projects a subject already owned through the stored Children edge.
        var subject = new ProjectingDerivedSubject(context);
        subject.Children = [new Person { FirstName = "C" }];

        // Assert
        Assert.NotNull(subject.FirstChild);
        Assert.NotNull(subject.FirstChild.TryGetContext());
    }

    [Fact]
    public void WhenAnObjectDeclaredDerivedPropertyReturnsAString_ThenItDoesNotThrow()
    {
        // Arrange
        var context = CreateDerivedContext();

        // Act
        var subject = new ObjectDerivedStringSubject(context);
        subject.Name = "world";

        // Assert
        Assert.Equal("Hello, world", subject.Value);
    }

    [Fact]
    public void WhenAnObjectDeclaredDerivedPropertyReturnsASubject_ThenItRemainsUnowned()
    {
        // Arrange
        var context = CreateDerivedContext();

        // Act
        var subject = new ObjectDerivedLazySubject(context);

        // Assert
        var projected = Assert.IsType<Person>(subject.Value);
        Assert.Null(projected.TryGetContext());
        Assert.Equal(0, projected.GetReferenceCount());
    }

    [Fact]
    public void WhenAComputedProjectionReturnsADetachedSubjectOnce_ThenThatValueCanPublish()
    {
        // Arrange
        var context = CreateDerivedContext();
        var subject = new TransientOrphanDerivedSubject(context);
        subject.ReturnUnattachedSubjectOnce = true;

        // Act
        subject.Name = "x";

        // Assert
        var data = new PropertyReference(subject, nameof(TransientOrphanDerivedSubject.Current)).GetDerivedPropertyData();
        var projected = Assert.IsType<Person>(data.LastKnownValue);
        Assert.Null(projected.TryGetContext());
        Assert.Equal(0, projected.GetReferenceCount());
        Assert.Null(subject.Current);
    }

    [Fact]
    public void WhenAProjectionKeepsAReleasedChild_ThenRecalculationPreservesTheValueWithoutAnEdge()
    {
        // Arrange
        var context = CreateDerivedContext();
        var subject = new CachingOrphanDerivedSubject(context);
        var child = new Person { FirstName = "C" };
        subject.Stored = child;
        Assert.Same(child, subject.Current);

        // Act
        subject.Stored = null;

        // Assert
        Assert.Same(child, subject.Current);
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenADerivedPropertyWithABackingFieldStoresASubject_ThenItOwnsTheSubject()
    {
        // Arrange: a derived property with a generator-emitted backing field is the sole store of
        // whatever is assigned, so it carries an ownership edge like any other stored property.
        var context = CreateDerivedContext();
        var subject = new StoringDerivedSubject(context);
        var child = new Person { FirstName = "Child" };

        // Act
        var exception = Record.Exception(() => subject.Current = child);

        // Assert
        Assert.Null(exception);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenADerivedPropertyWithABackingFieldReleasesASubject_ThenTheSubjectIsDetached()
    {
        // Arrange: clearing the store runs the derived-with-setter recalculation and the release
        // descent over the same write, so the edge must come off exactly once.
        var context = CreateDerivedContext();
        var subject = new StoringDerivedSubject(context);
        var child = new Person { FirstName = "Child" };
        subject.Current = child;

        // Act
        subject.Current = null;

        // Assert
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenAttachCallbackInitializesStructuralProperty_ThenChildIsOwnedBeforeNestedSetterReturns()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new Person(context);
        var child = new Person();
        var grandchild = new Person();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var nestedReturned = false;
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                child.Father = grandchild;
                Assert.Same(context, grandchild.TryGetContext());
                Assert.Equal(1, grandchild.GetReferenceCount());
                nestedReturned = true;
            }
        };

        // Act
        root.Father = child;

        // Assert
        Assert.True(nestedReturned);
        Assert.Same(grandchild, child.Father);
        Assert.Equal(1, grandchild.GetReferenceCount());
    }

    [Fact]
    public void WhenAttachCallbackReplacesItself_ThenDetachAndAttachAreDeliveredAfterCurrentCallback()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new Person(context);
        var first = new Person();
        var second = new Person();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var events = new List<string>();
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, first))
            {
                events.Add("first-start");
                root.Father = second;
                Assert.Equal(1, second.GetReferenceCount());
                events.Add("first-end");
            }
            else if (ReferenceEquals(change.Subject, second)) events.Add("second-attach");
        };
        lifecycle.SubjectDetaching += change => { if (ReferenceEquals(change.Subject, first)) events.Add("first-detach"); };

        // Act
        root.Father = first;

        // Assert
        Assert.Equal(new[] { "first-start", "first-end", "first-detach", "second-attach" }, events);
        Assert.Null(first.TryGetContext());
        Assert.Same(second, root.Father);
    }

    [Fact]
    public void WhenDetachCallbackReplacesSameProperty_ThenTransitionsAreFifoAndFinalEdgesAgree()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var oldChild = new Person();
        var root = new Person(context) { Father = oldChild };
        var intermediate = new Person();
        var final = new Person();
        var events = new List<string>();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, oldChild))
            {
                events.Add("old-detach");
                Assert.Same(intermediate, root.Father);
                Assert.Equal(1, intermediate.GetReferenceCount());
                root.Father = final;
                Assert.Same(final, root.Father);
                Assert.Equal(1, final.GetReferenceCount());
            }
            else if (ReferenceEquals(change.Subject, intermediate)) events.Add("intermediate-detach");
        };
        lifecycle.SubjectAttached += change => events.Add(ReferenceEquals(change.Subject, intermediate) ? "intermediate-attach" : "final-attach");

        // Act
        root.Father = intermediate;

        // Assert
        Assert.Equal(new[] { "old-detach", "intermediate-attach", "intermediate-detach", "final-attach" }, events);
        Assert.Same(final, root.Father);
        Assert.Null(oldChild.TryGetContext());
        Assert.Null(intermediate.TryGetContext());
        Assert.Equal(1, final.GetReferenceCount());
        Assert.False(lifecycle.Graph.IsOwned(intermediate));
    }

    [Fact]
    public void WhenDetachCallbackThrows_ThenReplacementAndRemainingDetachStillSettle()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var descendant = new Person();
        var oldChild = new Person { Father = descendant };
        var root = new Person(context) { Father = oldChild };
        var replacement = new Person();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var replacementNotified = false;
        lifecycle.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, oldChild)) throw new InvalidOperationException("callback failure");
        };
        lifecycle.SubjectAttached += change => replacementNotified |= ReferenceEquals(change.Subject, replacement);

        // Act
        var exception = Record.Exception(() => root.Father = replacement);

        // Assert
        Assert.NotNull(exception);
        Assert.Same(replacement, root.Father);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(oldChild.TryGetContext());
        Assert.Null(descendant.TryGetContext());
        Assert.False(lifecycle.Graph.IsOwned(descendant));
        Assert.True(replacementNotified);
    }

    [Fact]
    public void WhenDetachCallbackReattachesSameSubject_ThenDeferredReleaseDoesNotDropNewOwnership()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var child = new Person();
        var root = new Person(context) { Father = child };
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                Assert.Same(context, child.GetContext());
                root.Father = child;
            }
        };

        // Act
        root.Father = null;

        // Assert
        Assert.Same(child, root.Father);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenDetachCallbackReattachesAndRemovesSameSubject_ThenEveryHistoricalCallbackRetainsContext()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person();
        var root = new Person(context) { Father = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var events = new List<string>();
        var detachCount = 0;
        lifecycle.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, child)) return;
            Assert.Same(context, child.GetContext());
            Assert.True(lifecycle.Graph.IsReleasing(child));
            events.Add($"detach-{++detachCount}");
            if (detachCount == 1)
            {
                root.Father = child;
                root.Father = null;
                events.Add("nested-setters-returned");
            }
        };
        lifecycle.SubjectAttached += change =>
        {
            if (!ReferenceEquals(change.Subject, child)) return;
            Assert.Same(context, child.GetContext());
            events.Add("reattach");
        };

        // Act
        root.Father = null;

        // Assert
        Assert.Equal(["detach-1", "nested-setters-returned", "reattach", "detach-2"], events);
        Assert.Null(root.Father);
        Assert.Null(child.TryGetContext());
        Assert.Null(child.TryGetRegisteredSubject());
        Assert.Equal(0, child.GetReferenceCount());
        Assert.False(lifecycle.Graph.IsOwned(child));
        Assert.False(lifecycle.Graph.IsReleasing(child));
    }

    [Fact]
    public void WhenDetachCallbackExplicitlyAttachesDepartingSubject_ThenItOwnsItsDescendantsAsARoot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var descendant = new Person();
        var child = new Person { Father = descendant };
        var parent = new Person(context) { Father = child };
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var resurrected = false;
        lifecycle.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, child) || resurrected) return;
            resurrected = true;
            child.AttachToContext(context);
            Assert.True(lifecycle.Graph.IsOwned(child));
            Assert.True(lifecycle.Graph.IsOwned(descendant));
            Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)child).Executor.AttachmentAnchor);
        };

        // Act
        parent.Father = null;

        // Assert
        Assert.True(resurrected);
        Assert.Null(parent.Father);
        Assert.Same(context, child.GetContext());
        Assert.Same(context, descendant.GetContext());
        Assert.NotNull(child.TryGetRegisteredSubject());
        Assert.NotNull(descendant.TryGetRegisteredSubject());
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)child).Executor.AttachmentAnchor);
        Assert.Equal(1, descendant.GetReferenceCount());
        Assert.False(lifecycle.Graph.IsReleasing(child));
        Assert.False(lifecycle.Graph.IsReleasing(descendant));

        // Act
        child.DetachFromContext(context);

        // Assert
        Assert.Null(child.TryGetContext());
        Assert.Null(descendant.TryGetContext());
        Assert.Null(child.TryGetRegisteredSubject());
        Assert.Null(descendant.TryGetRegisteredSubject());
        Assert.False(lifecycle.Graph.IsOwned(child));
        Assert.False(lifecycle.Graph.IsOwned(descendant));
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)child).Executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenADetachCallbackReattachesTheDepartingSubjectAndItsGetterThrows_ThenTheRejectedRootAndDescendantsAreReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var descendant = new PayloadNode();
        var child = new ReadHookSegmentHolder { Children = new([descendant]) };
        var parent = new PayloadNode(context) { Payload = child };
        var failure = new InvalidOperationException("resurrection getter");
        var attempted = false;
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, child)) return;
            Assert.Same(context, child.GetContext());
            if (attempted) return;
            attempted = true;
            child.OnRead = () => throw failure;
            child.AttachToContext(context);
        };

        // Act
        var exception = Record.Exception(() => parent.Payload = null);
        child.OnRead = null;

        // Assert
        Assert.Same(failure, exception);
        Assert.True(attempted);
        Assert.Null(child.TryGetContext());
        Assert.Null(descendant.TryGetContext());
        Assert.Null(child.TryGetRegisteredSubject());
        Assert.Null(descendant.TryGetRegisteredSubject());
        Assert.False(lifecycle.Graph.IsOwned(child));
        Assert.False(lifecycle.Graph.IsOwned(descendant));
        Assert.False(lifecycle.Graph.IsReleasing(child));
        Assert.Equal(SubjectAttachmentAnchorKind.None, child.Executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenADetachCallbackReattachesTheDepartingSubjectAndADescendantGetterThrows_ThenEveryPublishedEdgeIsRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var oldDescendant = new PayloadNode();
        var child = new ReadHookSegmentHolder { Children = new([oldDescendant]) };
        var parent = new PayloadNode(context) { Payload = child };
        var accepted = new PayloadNode();
        var failingDescendant = new ReadHookSegmentHolder();
        var newDescendant = new PayloadNode { Payload = failingDescendant };
        var failure = new InvalidOperationException("descendant getter");
        var attempted = false;
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectDetaching += change =>
        {
            Assert.Same(context, change.Subject.GetContext());
            if (!ReferenceEquals(change.Subject, child) || attempted) return;
            attempted = true;
            child.Children = new([accepted, newDescendant]);
            failingDescendant.OnRead = () => throw failure;
            child.AttachToContext(context);
        };

        // Act
        var exception = Record.Exception(() => parent.Payload = null);
        failingDescendant.OnRead = null;

        // Assert
        Assert.Same(failure, exception);
        Assert.True(attempted);
        foreach (var subject in new IInterceptorSubject[] { child, oldDescendant, accepted, newDescendant, failingDescendant })
        {
            Assert.Null(subject.TryGetContext());
            Assert.Null(subject.TryGetRegisteredSubject());
            Assert.False(lifecycle.Graph.IsOwned(subject));
            Assert.False(lifecycle.Graph.IsReleasing(subject));
            Assert.Equal(0, subject.GetReferenceCount());
            Assert.Equal(SubjectAttachmentAnchorKind.None, subject.Executor.AttachmentAnchor);
        }
    }

    [Fact]
    public void WhenAnExplicitRootsAttachCallbackReplacesAChild_ThenTheNestedWriteSettles()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var stale = new PayloadNode();
        var replacement = new PayloadNode();
        var root = new PayloadNode { Child = stale };
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, root)) root.Child = replacement;
        };

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Same(replacement, root.Child);
        Assert.Null(stale.TryGetContext());
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
    }

    [Fact]
    public void WhenACallbackWritesIntoAnotherContext_ThenAStructuralWriteIsRejectedBeforeStorageAndAScalarWriteSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var foreignContext = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new Person(context);
        var foreign = new Person(foreignContext);
        Exception? rejected = null;
        context.TryGetLifecycleInterceptor()!.SubjectAttached += _ =>
        {
            foreign.FirstName = "ok";
            rejected = Record.Exception(() => foreign.Father = new Person());
        };

        // Act
        root.Father = new Person();

        // Assert
        Assert.IsType<LifecycleContractViolationException>(rejected);
        Assert.Null(foreign.Father);
        Assert.Equal("ok", foreign.FirstName);
    }
}

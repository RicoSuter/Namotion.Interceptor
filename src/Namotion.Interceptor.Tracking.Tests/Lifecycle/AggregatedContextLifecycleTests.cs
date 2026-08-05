using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The aggregated context: a child context carrying its own lifecycle services that also falls back
/// to a parent context carrying its own. <c>GetServices&lt;ILifecycleInterceptor&gt;()</c> then
/// resolves two <see cref="LifecycleInterceptor"/> instances and every one of them attaches every
/// subject, which is why ownership is claimed on graph membership rather than interceptor identity
/// (see "Reference Count and Graph Ownership" in docs/design/tracking-lifecycle.md).
///
/// Every test below says in its first comment whether it pins a guarantee the design intends or a
/// known limit it merely records. A failing limit test is not automatically a bug: it means either
/// someone improved the behaviour, in which case update the test and the design document
/// deliberately, or someone regressed it.
/// </summary>
public class AggregatedContextLifecycleTests
{
    private static (IInterceptorSubjectContext child, IInterceptorSubjectContext parent) CreateAggregatedContext()
    {
        var parentContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var childContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        childContext.AddFallbackContext(parentContext);
        return (childContext, parentContext);
    }

    [Fact]
    public void WhenAggregatedContextAttachesAndDetachesASubject_ThenOwnershipIsClaimedOnceAndReleasedOnce()
    {
        // Guarantee. Both co-resolved interceptors attach and both detach, yet exactly one holds the
        // claim while the subject is in the graph and nobody holds it afterwards. The reference count
        // is one per interceptor, so it is two here rather than one.

        // Arrange
        var (childContext, _) = CreateAggregatedContext();
        var interceptors = childContext.GetServices<ILifecycleInterceptor>();

        var parent = new Person(childContext) { FirstName = "P" };
        var child = new Person { FirstName = "C" };
        var executor = ((IInterceptorSubject)child).GetExecutor();

        // Act
        parent.Mother = child;

        var ownersWhileAttached = interceptors.Count(interceptor => executor.IsOwnedBy(interceptor));
        var countWhileAttached = child.GetReferenceCount();

        parent.Mother = null;

        // Assert
        Assert.Equal(2, interceptors.Length);
        Assert.Equal(1, ownersWhileAttached);
        Assert.Equal(2, countWhileAttached);

        Assert.DoesNotContain(interceptors, interceptor => executor.IsOwnedBy(interceptor));
        Assert.Equal(0, child.GetReferenceCount());
        Assert.False(((IInterceptorSubject)child).IsAttached());

        // The claim really is gone: a third, disjoint graph accepts the subject and gives it back.
        var thirdContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        ((IInterceptorSubject)child).AttachToContext(thirdContext);
        Assert.True(((IInterceptorSubject)child).IsAttached());

        ((IInterceptorSubject)child).DetachFromContext(thirdContext);
        Assert.False(((IInterceptorSubject)child).IsAttached());
    }

    [Fact]
    public void WhenSubjectIsRootAttachedThroughTheAggregatedContext_ThenAReferenceFromTheParentContextIsRejected()
    {
        // Known limit: the asymmetric claim predicate. AttachToContext runs the resolved interceptors
        // in order, so the child context's own interceptor claims first, and the parent context alone
        // does not resolve it. This is the rejecting direction.

        // Arrange
        var (childContext, parentContext) = CreateAggregatedContext();

        var subject = new Person { FirstName = "S" };
        ((IInterceptorSubject)subject).AttachToContext(childContext);

        var parentContextHolder = new Person(parentContext) { FirstName = "H" };

        // Act & Assert
        Assert.True(((IInterceptorSubject)subject).GetExecutor().IsOwnedBy(childContext.GetServices<ILifecycleInterceptor>()[0]));
        Assert.Throws<InvalidOperationException>(() => parentContextHolder.Mother = subject);

        // The rejected subject keeps exactly the state its own graph gave it, because ClaimOwnership
        // runs ahead of every mutation. What is not undone is the write itself: WriteProperty
        // committed it through next() before the lock, which is the mid-batch shape of #384.
        Assert.Equal(0, subject.GetReferenceCount());
        Assert.Same(childContext, ((IInterceptorSubject)subject).TryGetAttachContext());
        Assert.Same(subject, parentContextHolder.Mother);
    }

    [Fact]
    public void WhenSubjectIsRootAttachedThroughTheParentContext_ThenAReferenceFromTheAggregatedContextIsAccepted()
    {
        // Known limit: the other half of the asymmetry. The aggregated context resolves the standing
        // owner, so the claim passes and the subject is held by two contexts at once, counted once
        // per interceptor.

        // Arrange
        var (childContext, parentContext) = CreateAggregatedContext();

        var subject = new Person { FirstName = "S" };
        ((IInterceptorSubject)subject).AttachToContext(parentContext);

        var aggregatedHolder = new Person(childContext) { FirstName = "H" };

        // Act
        aggregatedHolder.Mother = subject;

        // Assert
        Assert.Equal(2, subject.GetReferenceCount());
        Assert.Same(parentContext, ((IInterceptorSubject)subject).TryGetAttachContext());
    }

    [Fact]
    public void WhenSubjectIsOwnedThroughAnAggregatedParentProperty_ThenAReferenceFromTheParentContextIsAccepted()
    {
        // Known limit: which of the two outcomes a configuration gets depends on resolved interceptor
        // order, not on how the contexts nest. On the property route the claim is taken by the
        // innermost interceptor in the write chain, which is the parent context's, so the same pair of
        // contexts that rejects the reference above accepts it here.

        // Arrange
        var (childContext, parentContext) = CreateAggregatedContext();

        var aggregatedParent = new Person(childContext) { FirstName = "AP" };
        var parentContextHolder = new Person(parentContext) { FirstName = "H" };
        var subject = new Person { FirstName = "S" };

        aggregatedParent.Mother = subject;

        // Act
        parentContextHolder.Mother = subject;

        // Assert
        Assert.True(((IInterceptorSubject)subject).GetExecutor().IsOwnedBy(parentContext.GetServices<ILifecycleInterceptor>()[0]));
        Assert.Equal(3, subject.GetReferenceCount());
    }

    [Fact]
    public void WhenALifecycleHandlerReattachesDuringDetach_ThenTheReattachIsStillRejected()
    {
        // Guarantee, and narrower than "Known Gaps" predicts. ThrowIfDetachIsUnwinding is gated on
        // ownership, so the doc expects a re-attach during the non-owner's unwind to pass. It does
        // not: both co-resolved interceptors unwind inside the same write, the re-attach runs through
        // both of them, and the owning one has already removed the subject from its own ledger by
        // then, so its guard fires. The re-attach is rejected here exactly as on a single-interceptor
        // graph.

        // Arrange
        var (childContext, _) = CreateAggregatedContext();

        var parent = new Person(childContext) { FirstName = "Parent" };
        var newHome = new Person(childContext) { FirstName = "Home" };
        var moving = new Person { FirstName = "Move" };

        parent.Mother = moving;

        var removalCounts = new List<int>();
        childContext.WithService(() => new RecordingLifecycleHandler(change =>
        {
            if (change.IsPropertyReferenceRemoved && ReferenceEquals(change.Subject, moving))
            {
                removalCounts.Add(change.ReferenceCount);
                if (change.ReferenceCount == 0)
                {
                    newHome.Mother = moving;
                }
            }
        }));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => parent.Mother = null);

        // One removal event per interceptor, and the subject still leaves the graph completely even
        // though the handler threw, because the release sits in a finally.
        Assert.Equal([1, 0], removalCounts);
        Assert.Equal(0, moving.GetReferenceCount());
        Assert.False(((IInterceptorSubject)moving).IsAttached());
        Assert.Empty(((IInterceptorSubject)moving).Context.GetServices<IWriteInterceptor>());

        // The residue the rejection cannot undo: next() committed the re-attach write before the
        // guard ran, so the new parent's backing store points at a detached subject.
        Assert.Same(moving, newHome.Mother);
    }

    [Fact]
    public void WhenBothInheritanceHandlersDescend_ThenTheSubtreeAttachesOncePerInterceptorAndUnwindsCompletely()
    {
        // Guarantee. Two ContextInheritanceHandler instances each descend into both interceptors, so
        // the subtree is walked four times, yet each subject collects exactly one context attach per
        // interceptor and one reference per interceptor. The unwind is symmetric and repeatable.

        // Arrange
        var parentContext = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var childContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        childContext.AddFallbackContext(parentContext);

        var registry = parentContext.GetServices<ISubjectRegistry>()[0];
        var attaches = new List<IInterceptorSubject>();
        var detaches = new List<IInterceptorSubject>();
        childContext.WithService(() => new RecordingLifecycleHandler(change =>
        {
            if (change.IsContextAttach) attaches.Add(change.Subject);
            if (change.IsContextDetach) detaches.Add(change.Subject);
        }));

        var root = new Person(childContext) { FirstName = "Root" };
        var mid = new Person { FirstName = "Mid" };
        var leaf = new Person { FirstName = "Leaf" };
        mid.Mother = leaf;

        var usedByBaseline = UsedByContextsProbe.Count(((IInterceptorSubject)root).Context);

        // Act
        root.Mother = mid;

        // Assert
        Assert.Equal(2, childContext.GetServices<ContextInheritanceHandler>().Length);
        Assert.Equal(2, childContext.GetServices<ILifecycleInterceptor>().Length);

        Assert.Equal(2, attaches.Count(subject => ReferenceEquals(subject, mid)));
        Assert.Equal(2, attaches.Count(subject => ReferenceEquals(subject, leaf)));
        Assert.Equal(2, mid.GetReferenceCount());
        Assert.Equal(2, leaf.GetReferenceCount());

        // One registry entry each, no duplicates: root, mid, leaf.
        Assert.Equal(3, registry.KnownSubjects.Count);
        Assert.Contains(mid, registry.KnownSubjects.Keys);
        Assert.Contains(leaf, registry.KnownSubjects.Keys);

        // Act: the whole subtree leaves again.
        root.Mother = null;

        // Assert
        Assert.Equal(2, detaches.Count(subject => ReferenceEquals(subject, mid)));
        Assert.Equal(2, detaches.Count(subject => ReferenceEquals(subject, leaf)));
        Assert.Equal(0, mid.GetReferenceCount());
        Assert.Equal(0, leaf.GetReferenceCount());
        Assert.Single(registry.KnownSubjects);
        Assert.Empty(((IInterceptorSubject)mid).Context.GetServices<IWriteInterceptor>());
        Assert.Empty(((IInterceptorSubject)leaf).Context.GetServices<IWriteInterceptor>());
        Assert.Equal(usedByBaseline, UsedByContextsProbe.Count(((IInterceptorSubject)root).Context));

        // A second cycle produces the same numbers, so nothing drifts per round.
        root.Mother = mid;
        Assert.Equal(2, mid.GetReferenceCount());
        Assert.Equal(2, leaf.GetReferenceCount());
        Assert.Equal(3, registry.KnownSubjects.Count);

        root.Mother = null;
        Assert.Equal(0, mid.GetReferenceCount());
        Assert.Single(registry.KnownSubjects);
    }

    [Fact]
    public void WhenTwoInterceptorsAttachOneParentReference_ThenTheRegistryRecordsThatParentTwice()
    {
        // Known limit, and not introduced by this design: AttachToProperty invokes the lifecycle
        // handlers once per interceptor on master too. A single registry shared by an aggregated
        // context therefore lists the same parent property once per interceptor, where a
        // single-interceptor context lists it once. It unwinds completely, so it is a doubling and
        // not a leak.

        // Arrange
        var parentContext = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var childContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        childContext.AddFallbackContext(parentContext);

        var registry = parentContext.GetServices<ISubjectRegistry>()[0];
        var root = new Person(childContext) { FirstName = "Root" };
        var child = new Person { FirstName = "Child" };

        // Act
        root.Mother = child;

        // Assert
        var parents = registry.KnownSubjects[child].Parents;
        Assert.Equal(2, parents.Length);
        Assert.Equal(parents[0], parents[1]);

        root.Mother = null;
        Assert.DoesNotContain(child, registry.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenTheDescentReachesAChildBeforeItsParent_ThenOneContextAttachCarriesNoProperty()
    {
        // Known limit. The descent calls AttachSubjectToContext on every resolved interceptor, and an
        // interceptor that has not yet seen the subtree root falls through to AttachRootSubject for
        // the grandchild it discovers. A handler in an aggregated context therefore sees one of the
        // grandchild's two context attaches with a null Property, as though it were a root, and the
        // other with the property that actually holds it.

        // Arrange
        var (childContext, _) = CreateAggregatedContext();

        var contextAttaches = new List<SubjectLifecycleChange>();
        childContext.WithService(() => new RecordingLifecycleHandler(change =>
        {
            if (change.IsContextAttach) contextAttaches.Add(change);
        }));

        var root = new Person(childContext) { FirstName = "Root" };
        var mid = new Person { FirstName = "Mid" };
        var leaf = new Person { FirstName = "Leaf" };
        mid.Mother = leaf;

        // Act
        root.Mother = mid;

        // Assert
        var leafAttaches = contextAttaches.Where(change => ReferenceEquals(change.Subject, leaf)).ToArray();
        Assert.Equal(2, leafAttaches.Length);
        Assert.Single(leafAttaches, change => change.Property is null);
        Assert.Single(leafAttaches, change => change.Property?.Name == nameof(Person.Mother));
    }

    [Fact]
    public void WhenTheContextIsAggregated_ThenTryGetLifecycleInterceptorThrows()
    {
        // Known limit. TryGetLifecycleInterceptor resolves through TryGetService, which throws when
        // two services of a type resolve. SourceOwnershipManager, OpcUaSubjectServer and
        // MqttSubjectServer all call it, so a consumer aggregating two lifecycle-bearing contexts
        // cannot construct any of them. Pinned here so that fact arrives as a test rather than as a
        // stack trace.

        // Arrange
        var (childContext, parentContext) = CreateAggregatedContext();
        var subject = new Person(childContext) { FirstName = "S" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => childContext.TryGetLifecycleInterceptor());
        Assert.Throws<InvalidOperationException>(() => ((IInterceptorSubject)subject).Context.TryGetLifecycleInterceptor());

        // The parent context alone resolves one, so the throw is a property of the aggregate.
        Assert.NotNull(parentContext.TryGetLifecycleInterceptor());
    }

    [Fact]
    public void WhenTheAttachContextSitsBelowTheGraphContext_ThenItsExtraInterceptorNeverSeesTheDetach()
    {
        // Known limit. A subject attached through a context that itself falls back to the graph's
        // context records that context's whole interceptor set, including one the graph does not
        // resolve. The property detach releases the attach edge through ReleaseAttachEdge, which
        // removes the edge without calling anyone, and the descent resolves only from the parent's
        // context, so the extra interceptor is told about the attach and never about the detach. It
        // cannot recover the notification afterwards either: the record is already gone, so
        // DetachFromContext is a silent no-op.

        // Arrange
        var (childContext, _) = CreateAggregatedContext();

        var recorder = new RecordingLifecycleInterceptor();
        var attachContext = InterceptorSubjectContext.Create();
        attachContext.WithService(() => recorder);
        attachContext.AddFallbackContext(childContext);

        var parent = new Person(childContext) { FirstName = "Parent" };
        var item = new Person { FirstName = "Item" };

        ((IInterceptorSubject)item).AttachToContext(attachContext);
        parent.Mother = item;

        // Act
        parent.Mother = null;

        // Assert
        Assert.Equal([item], recorder.Attaches);
        Assert.Empty(recorder.Detaches);

        Assert.Equal(0, item.GetReferenceCount());
        Assert.False(((IInterceptorSubject)item).IsAttached());
        Assert.Null(((IInterceptorSubject)item).TryGetAttachContext());
        Assert.Empty(((IInterceptorSubject)item).Context.GetServices<IWriteInterceptor>());

        ((IInterceptorSubject)item).DetachFromContext(attachContext);
        Assert.Empty(recorder.Detaches);
    }

    [Fact]
    public void WhenTheDetachResolvesThroughACyclicContext_ThenTheReleaseDoesNotMaskTheFailure()
    {
        // Guarantee. The release runs in a finally, so anything it throws replaces the exception
        // already in flight and leaves the claim standing. Here the subject is root-attached
        // through a pure delegator, that delegator is then rewired into a delegation loop, and the
        // detach is driven directly on the interceptor that does not hold the claim. Every service
        // resolution through the subject's own context now raises, so the release may consult only
        // what the claim recorded.
        //
        // SubjectDetaching supplies the try block's failure because the failure the try produces on
        // its own and the masking one are both the delegation-cycle InvalidOperationException and
        // could not be told apart.

        // Arrange
        var (childContext, _) = CreateAggregatedContext();
        var delegatingContext = InterceptorSubjectContext.Create();
        delegatingContext.AddFallbackContext(childContext);

        var subject = (IInterceptorSubject)new PropertyLessSubject();
        subject.AttachToContext(delegatingContext);

        var interceptors = delegatingContext.GetServices<ILifecycleInterceptor>();
        var executor = subject.GetExecutor();
        var nonOwner = (LifecycleInterceptor)interceptors.Single(interceptor => !executor.IsOwnedBy(interceptor));
        nonOwner.SubjectDetaching += _ => throw new DetachMarkerException();

        // The subject's own context is a pure delegator into the loop from here on.
        delegatingContext.AddFallbackContext(delegatingContext);
        delegatingContext.RemoveFallbackContext(childContext);

        // Act
        var exception = Record.Exception(() => nonOwner.DetachSubjectFromContext(subject));

        // Assert
        Assert.IsType<DetachMarkerException>(exception);
        Assert.DoesNotContain(interceptors, interceptor => executor.IsOwnedBy(interceptor));

        // The claim really is gone: with the chain repaired the subject leaves and joins a third,
        // disjoint graph.
        delegatingContext.AddFallbackContext(childContext);
        delegatingContext.RemoveFallbackContext(delegatingContext);
        subject.DetachFromContext(delegatingContext);

        var thirdContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        subject.AttachToContext(thirdContext);
        Assert.True(subject.IsAttached());
    }

    [Fact]
    public void WhenTheInterceptorBringingTheCountToZeroIsNotTheOwner_ThenTheRecordedClaimReleasesOwnership()
    {
        // Guarantee, and the reason the release never has to resolve anything. On the property
        // route the claim is taken by the innermost interceptor in the write chain while the count
        // reaches zero under the outermost one, so the release travels through the set recorded at
        // claim time rather than short-circuiting on interceptor identity.

        // Arrange
        var (childContext, _) = CreateAggregatedContext();
        var interceptors = childContext.GetServices<ILifecycleInterceptor>();

        var parent = new Person(childContext) { FirstName = "P" };
        var child = new Person { FirstName = "C" };
        var executor = ((IInterceptorSubject)child).GetExecutor();

        ILifecycleInterceptor? releasingInterceptor = null;
        foreach (var interceptor in interceptors)
        {
            var current = (LifecycleInterceptor)interceptor;
            current.SubjectDetaching += change =>
            {
                if (change.ReferenceCount == 0 && ReferenceEquals(change.Subject, child))
                {
                    releasingInterceptor = current;
                }
            };
        }

        // Act
        parent.Mother = child;
        var ownerWhileAttached = interceptors.Single(interceptor => executor.IsOwnedBy(interceptor));
        parent.Mother = null;

        // Assert
        Assert.NotNull(releasingInterceptor);
        Assert.NotSame(ownerWhileAttached, releasingInterceptor);

        Assert.DoesNotContain(interceptors, interceptor => executor.IsOwnedBy(interceptor));
        Assert.Equal(0, child.GetReferenceCount());
        Assert.False(((IInterceptorSubject)child).IsAttached());
    }

    private class DetachMarkerException : Exception;

    private class RecordingLifecycleHandler(Action<SubjectLifecycleChange> onChange) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change) => onChange(change);
    }

    private class RecordingLifecycleInterceptor : ILifecycleInterceptor
    {
        public List<IInterceptorSubject> Attaches { get; } = [];

        public List<IInterceptorSubject> Detaches { get; } = [];

        public void AttachSubjectToContext(IInterceptorSubject subject) => Attaches.Add(subject);

        public void DetachSubjectFromContext(IInterceptorSubject subject) => Detaches.Add(subject);
    }
}

/// <summary>
/// A subject with no properties at all. <c>DetachRootSubject</c> resolves
/// <c>IPropertyLifecycleHandler</c> once per property ahead of its try block, so a single property
/// would raise the delegation-cycle exception before the try and the masking finally would never
/// be reached.
/// </summary>
[InterceptorSubject]
public partial class PropertyLessSubject
{
}

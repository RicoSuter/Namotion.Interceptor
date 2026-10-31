using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;
using static Namotion.Interceptor.Tracking.Tests.Lifecycle.SupportContractAssertions;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CommitThenNotifyBoundaryTests
{
    [Fact]
    public void WhenAnAttachCallbackPromotesAnAdoptedSubjectAndThrows_ThenTheExplicitAnchorSurvivesParentRemoval()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var grandchild = new Person();
        var provisional = new Person(context) { Father = grandchild };
        var refusedChild = new Person();
        var root = new Person { Father = provisional, Mother = refusedChild };
        var failure = new InvalidOperationException("attach callback failed after promotion");
        SubjectAttachmentAnchorKind? anchorBeforePromotion = null;
        var promotionReturned = false;
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (!ReferenceEquals(change.Subject, refusedChild)) return;
            anchorBeforePromotion = ((IInterceptorSubject)provisional).Executor.AttachmentAnchor;
            provisional.AttachToContext(context);
            promotionReturned = true;
            throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.Same(failure, exception);
        Assert.True(promotionReturned);
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorBeforePromotion);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
        AssertAttached(context, root, 0);
        AssertAttached(context, provisional, 1);
        AssertAttached(context, grandchild, 1);
        AssertAttached(context, refusedChild, 1);
        Assert.Equal(4, Registry(context).KnownSubjects.Count);

        // Act
        root.DetachFromContext(context);

        // Assert
        AssertReleased(context, root, refusedChild);
        AssertAttached(context, provisional, 0);
        AssertAttached(context, grandchild, 1);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
        Assert.Equal(2, Registry(context).KnownSubjects.Count);
        Assert.Same(grandchild, Assert.Single(Registry(context).TryGetRegisteredSubject(provisional)!
            .TryGetProperty(nameof(Person.Father))!.Children).Subject);

        // Act
        provisional.DetachFromContext(context);

        // Assert
        AssertReleased(context, provisional, grandchild);
        Assert.Empty(Registry(context).KnownSubjects);
    }

    [Fact]
    public void WhenAnExplicitDetachCallbackThrows_ThenTheEntireComponentIsReleasedBeforeTheFailureEscapes()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var grandchild = new Person();
        var child = new Person { Father = grandchild };
        var root = new Person { Father = child };
        root.AttachToContext(context);
        var failure = new InvalidOperationException("detach callback failed");
        var callbackCount = 0;
        IInterceptorSubjectContext? callbackContext = null;
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, child)) return;
            callbackCount++;
            callbackContext = child.GetContext();
            throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.DetachFromContext(context));

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(1, callbackCount);
        Assert.Same(context, callbackContext);
        AssertReleased(context, root, child, grandchild);
        Assert.Empty(Registry(context).KnownSubjects);

        // Act
        var laterRoot = new Person { Father = new Person() };
        laterRoot.AttachToContext(context);
        laterRoot.DetachFromContext(context);

        // Assert
        Assert.Equal(1, callbackCount);
        AssertReleased(context, laterRoot, laterRoot.Father);
        Assert.Empty(Registry(context).KnownSubjects);
    }

    [Fact]
    public void WhenADetachCallbackAdmitsMetadataOnAReleasingHost_ThenNoEdgeOrRegistryResidueConsumesTheProvisionalRoot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new Person();
        root.AttachToContext(context);
        var host = new Person();
        var sibling = new Person();
        root.Children = [host, sibling];
        var grandchild = new Person();
        var provisional = new Person(context) { Father = grandchild };
        var revision = ((IInterceptorSubject)provisional).Executor.AttachmentRevision;
        var graph = context.TryGetLifecycleInterceptor()!.Graph;
        var admittedProperty = new PropertyReference(host, "Adopted");
        var admittedCallbacks = 0;
        context.WithService(() => new DelegatePropertyAttachHandler(change =>
        {
            if (change.Property.Equals(admittedProperty)) admittedCallbacks++;
        }));
        var admissionReturned = false;
        var hostWasReleasing = false;
        var hostReferences = -1;
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, sibling)) return;
            hostWasReleasing = graph.IsReleasing(host) && !graph.IsOwned(host) &&
                               ReferenceEquals(host.TryGetContext(), context);
            hostReferences = host.GetReferenceCount();
            ((IInterceptorSubject)host).AddProperties(new SubjectPropertyMetadata(
                "Adopted", typeof(Person), [], _ => provisional, null, isIntercepted: true, isDynamic: true));
            admissionReturned = true;
        };

        // Act
        root.Children = [];

        // Assert
        Assert.True(admissionReturned);
        Assert.True(hostWasReleasing);
        Assert.Equal(0, hostReferences);
        Assert.Equal(0, admittedCallbacks);
        Assert.True(((IInterceptorSubject)host).Properties.ContainsKey("Adopted"));
        Assert.False(graph.HasBaseline(admittedProperty));
        AssertReleased(context, host, sibling);
        AssertAttached(context, root, 0);
        AssertAttached(context, provisional, 0);
        AssertAttached(context, grandchild, 1);
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
        Assert.Equal(revision, ((IInterceptorSubject)provisional).Executor.AttachmentRevision);
        Assert.Equal(3, Registry(context).KnownSubjects.Count);

        // Act
        provisional.AttachToContext(context);
        provisional.DetachFromContext(context);
        root.DetachFromContext(context);

        // Assert
        AssertReleased(context, provisional, grandchild, root);
        Assert.Empty(Registry(context).KnownSubjects);
    }

    [Fact]
    public void WhenAnAttachCallbackFailsAfterABackEdgeAttachedTheRoot_ThenTheCommittedComponentRemainsDetachable()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var root = new Person { FirstName = "root" };
        var childA = new Person { FirstName = "A" };
        var childB = new Person { FirstName = "B" };

        root.Father = childA;
        childA.Father = root;
        root.Mother = childB;
        var failure = new InvalidOperationException("callback refuses childB");

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, childB))
            {
                throw failure;
            }
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        SupportContractAssertions.Settled(context, [root], root, childA, childB);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, childA, childB);
    }

    [Fact]
    public void WhenAnAttachCallbackFailsAfterConsumingAProvisionalAnchor_ThenTheAdoptedSubtreeRemainsCommitted()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var provisionalRoot = new Person(context) { FirstName = "P" };
        var grandchild = new Person { FirstName = "G" };
        provisionalRoot.Father = grandchild;

        var refused = new Person { FirstName = "X" };
        var root = new Person { FirstName = "R", Father = provisionalRoot, Mother = refused };

        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var provisionalSubject = (IInterceptorSubject)provisionalRoot;
        var grandchildSubject = (IInterceptorSubject)grandchild;
        var detached = new List<IInterceptorSubject>();
        SubjectAttachmentAnchorKind? anchorAtRefusal = null;
        lifecycle.SubjectDetaching += change => detached.Add(change.Subject);
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                anchorAtRefusal = provisionalSubject.Executor.AttachmentAnchor;
                throw new InvalidOperationException("callback refuses X");
            }
        };

        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, provisionalSubject.Executor.AttachmentAnchor);
        Assert.Same(context, grandchildSubject.TryGetContext());

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("callback refuses X", exception.Message);
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorAtRefusal);
        Assert.Equal(SubjectAttachmentAnchorKind.None, provisionalSubject.Executor.AttachmentAnchor);
        Assert.Empty(detached);
        SupportContractAssertions.Settled(context, [root], root, provisionalRoot, grandchild, refused);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, provisionalRoot, grandchild, refused);
    }

    [Fact]
    public void WhenAnAttachCallbackFailsAfterConsumingSeveralProvisionalAnchors_ThenEverySubtreeRemainsCommitted()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var first = new Person(context) { FirstName = "P1", Father = new Person { FirstName = "G1" } };
        var second = new Person(context) { FirstName = "P2", Father = new Person { FirstName = "G2" } };
        var refused = new Person { FirstName = "X" };
        var root = new Person { FirstName = "R", Father = first, Mother = second, Children = [refused] };

        var anchorsAtRefusal = new List<SubjectAttachmentAnchorKind>();
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                anchorsAtRefusal.Add(((IInterceptorSubject)first).Executor.AttachmentAnchor);
                anchorsAtRefusal.Add(((IInterceptorSubject)second).Executor.AttachmentAnchor);
                throw new InvalidOperationException("callback refuses X");
            }
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("callback refuses X", exception.Message);
        Assert.Equal([SubjectAttachmentAnchorKind.None, SubjectAttachmentAnchorKind.None], anchorsAtRefusal);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)first).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)second).Executor.AttachmentAnchor);
        IInterceptorSubject[] subjects = [root, first, second, first.Father, second.Father, refused];
        SupportContractAssertions.Settled(context, [root], subjects);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], subjects);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void WhenAnAttachCallbackFailsAfterAdoptingProvisionalRoots_ThenOnlyCommittedEdgesSupportThem(
        int provisionalCount, bool hasOutsideSupport)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var provisionalRoots = Enumerable.Range(0, provisionalCount)
            .Select(_ => new Person(context) { Father = new Person() }).ToArray();
        var grandchildren = provisionalRoots.Select(subject => subject.Father!).ToArray();
        var refusedChild = new Person();
        var root = new Person { Children = [.. provisionalRoots, refusedChild] };
        var outside = new Person();
        if (hasOutsideSupport)
        {
            outside.AttachToContext(context);
            outside.Father = provisionalRoots[0];
        }
        var failure = FailAttach(context, refusedChild);

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.Same(failure, exception);
        AssertAttached(context, root, 0);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        AssertAttached(context, refusedChild, 1);
        for (var index = 0; index < provisionalRoots.Length; index++)
        {
            var provisional = provisionalRoots[index];
            Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
            AssertAttached(context, provisional, hasOutsideSupport && index == 0 ? 2 : 1);
            AssertAttached(context, grandchildren[index], 1);
            AssertEdge(context, provisional, new PropertyReference(root, nameof(Person.Children)), index);
            AssertEdge(context, grandchildren[index], new PropertyReference(provisional, nameof(Person.Father)));
        }
        AssertEdge(context, refusedChild, new PropertyReference(root, nameof(Person.Children)), provisionalCount);
        if (hasOutsideSupport)
            AssertEdge(context, provisionalRoots[0], new PropertyReference(outside, nameof(Person.Father)));
        Assert.Equal(2 + 2 * provisionalCount + (hasOutsideSupport ? 1 : 0), Registry(context).KnownSubjects.Count);

        // Act
        root.DetachFromContext(context);

        // Assert
        AssertReleased(context, root, refusedChild);
        for (var index = hasOutsideSupport ? 1 : 0; index < provisionalRoots.Length; index++)
            AssertReleased(context, provisionalRoots[index], grandchildren[index]);
        if (hasOutsideSupport)
        {
            AssertAttached(context, outside, 0);
            AssertAttached(context, provisionalRoots[0], 1);
            AssertAttached(context, grandchildren[0], 1);
            AssertEdge(context, provisionalRoots[0], new PropertyReference(outside, nameof(Person.Father)));
            Assert.Equal(3, Registry(context).KnownSubjects.Count);

            // Act
            outside.Father = null;
            outside.DetachFromContext(context);

            // Assert
            AssertReleased(context, outside, provisionalRoots[0], grandchildren[0]);
        }
        Assert.Empty(Registry(context).KnownSubjects);
    }

    [Fact]
    public void WhenAnAttachCallbackFailsWithOutsideSupport_ThenBothIncomingEdgesRemainCommitted()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var provisional = new Person(context) { FirstName = "P" };
        var outside = new Person { FirstName = "E" };
        ((IInterceptorSubject)outside).AttachToContext(context);
        var refused = new Person { FirstName = "X" };

        var provisionalSubject = (IInterceptorSubject)provisional;
        var anchorsAroundTheOutsideEdge = new List<SubjectAttachmentAnchorKind>();
        var root = new EnumerableChildrenHolder();
        root.Children = new PhaseHookEnumerable(
            [provisional, refused],
            shouldRun: () => ((IInterceptorSubject)root).TryGetContext() is not null,
            onRun: () =>
            {
                anchorsAroundTheOutsideEdge.Add(provisionalSubject.Executor.AttachmentAnchor);
                outside.Father = provisional;
                anchorsAroundTheOutsideEdge.Add(provisionalSubject.Executor.AttachmentAnchor);
            });

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                throw new InvalidOperationException("callback refuses X");
            }
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("callback refuses X", exception.Message);
        Assert.Equal([SubjectAttachmentAnchorKind.Provisional, SubjectAttachmentAnchorKind.None], anchorsAroundTheOutsideEdge);
        Assert.Equal(SubjectAttachmentAnchorKind.None, provisionalSubject.Executor.AttachmentAnchor);
        Assert.Equal(2, provisionalSubject.GetReferenceCount());
        SupportContractAssertions.Settled(context, [root, outside], root, outside, provisional, refused);
        root.DetachFromContext(context);
        Assert.Equal(1, provisionalSubject.GetReferenceCount());
        SupportContractAssertions.Settled(context, [outside], root, outside, provisional, refused);
        outside.Father = null;
        SupportContractAssertions.Settled(context, [outside], root, outside, provisional, refused);
        outside.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, outside, provisional, refused);
    }

    [Fact]
    public void WhenANestedAttachCallbackFails_ThenBothRootsCommitBeforeTheOuterDrainThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var innerProvisional = new Person(context) { FirstName = "PI", Father = new Person { FirstName = "GI" } };
        var outerProvisional = new Person(context) { FirstName = "PO" };
        var refused = new Person { FirstName = "X" };
        var innerRoot = new Person { FirstName = "RI", Father = innerProvisional, Mother = refused };

        var outerRoot = new EnumerableChildrenHolder();
        Exception? innerException = null;
        outerRoot.Children = new PhaseHookEnumerable(
            [outerProvisional],
            shouldRun: () => ((IInterceptorSubject)outerRoot).TryGetContext() is not null,
            onRun: () => innerException = Record.Exception(() => ((IInterceptorSubject)innerRoot).AttachToContext(context)));

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                throw new InvalidOperationException("callback refuses X");
            }
        };

        // Act
        var outerException = Record.Exception(() => ((IInterceptorSubject)outerRoot).AttachToContext(context));

        // Assert
        Assert.Null(innerException);
        Assert.IsType<InvalidOperationException>(outerException);
        Assert.Equal("callback refuses X", outerException.Message);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)innerRoot).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)outerRoot).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)innerProvisional).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)outerProvisional).Executor.AttachmentAnchor);
        IInterceptorSubject[] subjects = [innerRoot, outerRoot, innerProvisional, outerProvisional, innerProvisional.Father, refused];
        SupportContractAssertions.Settled(context, [innerRoot, outerRoot], subjects);
        innerRoot.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [outerRoot], subjects);
        outerRoot.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], subjects);
    }

    [Fact]
    public void WhenAnAttachCallbackPromotesAnAdoptedCollectionItemAndThrows_ThenTheExplicitAnchorSurvivesParentRemoval()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var provisional = new Person(context) { FirstName = "P", Father = new Person { FirstName = "G" } };
        var refused = new Person { FirstName = "X" };
        var promotionReturned = false;

        var root = new EnumerableChildrenHolder();
        root.Children = new Person[] { provisional, refused };

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                provisional.AttachToContext(context);
                promotionReturned = true;
                throw new InvalidOperationException("callback refuses X");
            }
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("callback refuses X", exception.Message);
        Assert.True(promotionReturned);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
        IInterceptorSubject[] subjects = [root, provisional, provisional.Father, refused];
        SupportContractAssertions.Settled(context, [root, provisional], subjects);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [provisional], subjects);
        Assert.Equal(0, provisional.GetReferenceCount());
        provisional.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], subjects);
    }

    [Fact]
    public void WhenAnAdmittedPropertyCallbackFails_ThenItsPublishedEdgeAndRegistryProjectionRemainConsistent()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var failure = new InvalidOperationException("admission callback failed");
        var callbackCount = 0;
        context.WithService(() => new DelegatePropertyAttachHandler(change =>
        {
            if (change.Property.Name != "Poison") return;
            callbackCount++;
            throw failure;
        }));
        var root = new Person();
        root.AttachToContext(context);
        var child = new Person();
        var property = new PropertyReference(root, "Poison");

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AddProperties(
            new SubjectPropertyMetadata("Poison", typeof(Person), [], _ => child, null,
                isIntercepted: true, isDynamic: true)));

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(1, callbackCount);
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("Poison"));
        Assert.Same(child, context.TryGetLifecycleInterceptor()!.Graph.GetBaselineSnapshot(property).Value);
        AssertAttached(context, root, 0);
        AssertAttached(context, child, 1);
        AssertEdge(context, child, property);
        Assert.Equal(2, Registry(context).KnownSubjects.Count);

        // Act
        root.DetachFromContext(context);

        // Assert
        AssertReleased(context, root, child);
        Assert.Empty(Registry(context).KnownSubjects);
    }

    private static InvalidOperationException FailAttach(IInterceptorSubjectContext context, IInterceptorSubject target)
    {
        var failure = new InvalidOperationException("attach callback failed");
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, target)) throw failure;
        };
        return failure;
    }

    private static ISubjectRegistry Registry(IInterceptorSubjectContext context) => context.GetService<ISubjectRegistry>();

    private static void AssertEdge(IInterceptorSubjectContext context, IInterceptorSubject child, PropertyReference property, object? index = null)
    {
        Assert.Contains(child.GetParents(), parent => parent.Property.Equals(property) && Equals(parent.Index, index));
        var registered = Registry(context).TryGetRegisteredSubject(child)!;
        Assert.Contains(registered.Parents, parent => parent.Property.Reference.Equals(property) && Equals(parent.Index, index));
        var registeredProperty = Registry(context).TryGetRegisteredSubject(property.Subject)!.TryGetProperty(property.Name)!;
        Assert.Contains(registeredProperty.Children, item => ReferenceEquals(item.Subject, child) && Equals(item.Index, index));
    }

    /// <summary>
    /// A user enumerable that runs one action the first time it is scanned while the condition
    /// holds. The condition names the phase the test needs (the seed's scan once the root is
    /// claimed, or a callback after the attachment) rather than an enumeration ordinal,
    /// because how often a value is scanned is an implementation detail.
    /// </summary>
    private sealed class PhaseHookEnumerable(Person[] items, Func<bool> shouldRun, Action onRun) : IEnumerable<Person>
    {
        private bool _hasRun;

        public IEnumerator<Person> GetEnumerator()
        {
            if (!_hasRun && shouldRun())
            {
                _hasRun = true;
                onRun();
            }

            return ((IEnumerable<Person>)items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

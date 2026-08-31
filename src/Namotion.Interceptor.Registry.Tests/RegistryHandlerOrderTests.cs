using System.Collections;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Pins the registry's position ahead of the lifecycle descent for every composition, and what that
/// gives a handler observing a subject as it attaches. The merged <see cref="LifecycleInterceptor"/>
/// is the ordering seam, so the registry's <c>[RunsBefore(typeof(LifecycleInterceptor))]</c> is what
/// this order rests on; the chain assertions are the proof that the constraint binds, because with
/// an unbound constraint the "registry-last" composition would resolve the registry behind the
/// lifecycle.
/// </summary>
public class RegistryHandlerOrderTests
{
    public static TheoryData<string> RegistrationOrders() =>
    [
        "registry-first",
        "registry-after-tracking",
        "registry-after-lifecycle"
    ];

    private static IInterceptorSubjectContext CreateContext(string registrationOrder)
    {
        var context = InterceptorSubjectContext.Create();
        return registrationOrder switch
        {
            "registry-first" => context.WithRegistry().WithFullPropertyTracking(),
            "registry-after-tracking" => context.WithFullPropertyTracking().WithRegistry(),
            "registry-after-lifecycle" => context.WithLifecycle().WithRegistry(),
            _ => throw new ArgumentOutOfRangeException(nameof(registrationOrder))
        };
    }

    /// <summary>Builds root -> top -> middle -> child, with the subtree detached.</summary>
    private static OrderNode BuildDetachedSubtree(out OrderNode child)
    {
        child = new OrderNode { Name = "child" };
        var middle = new OrderNode { Name = "middle", Child = child };
        return new OrderNode { Name = "top", Child = middle };
    }

    [Theory]
    [MemberData(nameof(RegistrationOrders))]
    public void WhenRegistryIsRegisteredInAnyOrder_ThenTheHandlerChainResolvesIdentically(string registrationOrder)
    {
        // Arrange
        var context = CreateContext(registrationOrder);

        // Act
        var handlers = context.GetServices<ILifecycleHandler>()
            .Select(handler => handler.GetType().Name)
            .ToArray();

        // Assert
        Assert.Equal(
            [nameof(SubjectRegistry), nameof(LifecycleInterceptor)],
            handlers);
    }

    [Theory]
    [MemberData(nameof(RegistrationOrders))]
    public void WhenPrebuiltSubtreeIsAttachedToLiveRoot_ThenEveryAncestorIsRegistryVisibleDuringAttach(string registrationOrder)
    {
        // Arrange
        var context = CreateContext(registrationOrder);
        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out var child);

        // Act
        root.Child = top;

        // Assert: the registry runs ahead of the descent at every level, so when the child's own
        // handler runs, every ancestor up to the root is already registered.
        Assert.Equal(["middle", "top", "root"], child.AncestorsVisibleDuringAttach);
    }

    [Fact]
    public void WhenAPrepopulatedRootIsAttached_ThenRegistryResolvesItsChildThroughTheRootProjection()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var child = new Person { FirstName = "child" };
        var root = new Person { FirstName = "root", Father = child };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert
        Assert.Null(exception);
        var registeredRoot = root.TryGetRegisteredSubject();
        var registeredChild = child.TryGetRegisteredSubject();
        Assert.NotNull(registeredRoot);
        Assert.NotNull(registeredChild);
        var father = registeredRoot.TryGetProperty(nameof(Person.Father));
        Assert.NotNull(father);
        Assert.Same(child, Assert.Single(father.Children).Subject);
        Assert.Same(father, Assert.Single(registeredChild.Parents).Property);
    }

    [Fact]
    public void WhenAncestorsAreWalkedOverRegistryEdges_ThenTheWholeChainResolvesDuringAttach()
    {
        // Arrange: the same chain resolved over the registry's own parent edges rather than over
        // GetParents(), the shape the README and the connector docs use.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out var child);

        // Act
        root.Child = top;

        // Assert
        Assert.Equal(["middle", "top", "root"], child.AncestorsViaRegistryDuringAttach);
    }

    [Fact]
    public void WhenTheFirstHandlerObservesAnEdge_ThenGetParentsAlreadyReportsIt()
    {
        // Arrange: authoritative parent state is published before the first handler runs, so even
        // a handler ahead of the registry resolves the committed edge through GetParents().
        var probe = new FirstHandlerParentProbe();
        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        context.AddService<ILifecycleHandler>(probe);

        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out _);

        // Act
        root.Child = top;

        // Assert: one observation per attached edge (top, middle, child), each already visible.
        Assert.Equal(3, probe.EdgeVisibleInParents.Count);
        Assert.All(probe.EdgeVisibleInParents, Assert.True);
        Assert.Equal(
            nameof(FirstHandlerParentProbe),
            context.GetServices<ILifecycleHandler>()[0].GetType().Name);
    }

    [Fact]
    public void WhenSubtreeIsDetached_ThenAncestorsAreAlreadyDeregisteredButParentLinkRemains()
    {
        // Arrange
        var context = CreateContext("registry-after-tracking");
        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out var child);
        root.Child = top;

        // Act
        root.Child = null;

        // Assert: detach is deliberately not the mirror of attach. A subject leaving the graph has
        // already given up its ownership record when the first detach handler runs, so it reports no
        // parents at all, and its ancestors were processed further up the descent and are gone too.
        // A subject that survives an edge removal still reports the edges that remain. A consumer
        // needing ancestor state while detaching has to capture it at attach; the edge it is being
        // detached from is on the change itself.
        Assert.Equal(0, child.ParentLinkCountDuringDetach);
        Assert.Empty(child.AncestorsVisibleDuringDetach);
    }

    [Fact]
    public void WhenARegistryInitializerWaitsForRegistryAccess_ThenTheWorkerCompletes()
    {
        // Arrange
        var initializer = new RegistryLockProbeInitializer();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<ISubjectPropertyInitializer>(initializer);
        var registry = context.GetService<ISubjectRegistry>();
        var trigger = new Person { FirstName = "trigger" };
        initializer.Arm(trigger, registry);

        // Act
        var exception = Record.Exception(() => trigger.AttachToContext(context));

        // Assert
        Assert.Null(exception);
        Assert.True(initializer.CallbackReached);
        Assert.Null(initializer.WorkerException);
    }

    [Fact]
    public void WhenAttributeMetadataEnumeratesAgainDuringAttach_ThenItRunsOutsideTheRegistryLock()
    {
        // Arrange: SubjectPropertyMetadata consumes the first enumeration while it determines
        // whether the property is derived. The second enumeration is the Registry projection.
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var registry = context.GetService<ISubjectRegistry>();
        var subject = new Person { FirstName = "subject" };
        var attributes = new RegistryLockProbeAttributes(registry, subject);
        ((IInterceptorSubject)subject).AddProperties(new SubjectPropertyMetadata(
            "Probe", typeof(string), attributes, _ => "value", null,
            isIntercepted: true, isDynamic: true));

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)subject).AttachToContext(context));

        // Assert
        Assert.Null(exception);
        Assert.True(attributes.WorkerCompleted,
            "attribute metadata was enumerated while the Registry lock excluded its worker");
        Assert.Null(attributes.WorkerException);
        Assert.True(attributes.EnumerationCount >= 2);
    }

    [Fact]
    public async Task WhenAncestorAttachCallbacksArePending_ThenDescendantWriteRetriesAfterPublication()
    {
        // Arrange
        using var blocker = new BlockingAttachHandler();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<ILifecycleHandler>(blocker);
        var root = new OrderNode(context) { Name = "root" };
        var parent = new OrderNode { Name = "parent" };
        var child = new OrderNode { Name = "child" };
        blocker.Arm(parent);

        // Act
        var ancestorAttach = Task.Run(() => Record.Exception(() => root.Child = parent));
        Exception? conflictingWriteException;
        try
        {
            Assert.True(
                blocker.CallbackEntered.Wait(TimeSpan.FromSeconds(10)),
                "ancestor attach callback did not reach the blocking handler");
            conflictingWriteException = Record.Exception(() => parent.Child = child);
        }
        finally
        {
            blocker.ContinueCallback.Set();
        }

        var ancestorAttachException = await ancestorAttach.WaitAsync(TimeSpan.FromSeconds(10));
        var retryException = Record.Exception(() => parent.Child = child);

        // Assert
        Assert.IsType<LifecycleConflictException>(conflictingWriteException);
        Assert.Null(ancestorAttachException);
        Assert.Null(retryException);
        var registeredParent = parent.TryGetRegisteredSubject();
        var registeredChild = child.TryGetRegisteredSubject();
        Assert.NotNull(registeredParent);
        Assert.NotNull(registeredChild);
        var parentProperty = registeredParent.TryGetProperty(nameof(OrderNode.Child));
        Assert.NotNull(parentProperty);
        Assert.Same(child, Assert.Single(parentProperty.Children).Subject);
        Assert.Same(parentProperty, Assert.Single(registeredChild.Parents).Property);
    }

    [Fact]
    public async Task WhenRetainedChildCallbacksArePending_ThenExternalScalarWriteRetriesAfterPublication()
    {
        // Arrange
        using var blocker = new BlockingRetainedEdgeHandler();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<ILifecycleHandler>(blocker);
        var firstParent = new OrderNode(context) { Name = "first parent" };
        var secondParent = new OrderNode(context) { Name = "second parent" };
        var child = new OrderNode { Name = "child" };
        firstParent.Child = child;
        blocker.Arm(child, secondParent);

        // Act
        var edgeAddition = Task.Run(() => Record.Exception(() => secondParent.Child = child));
        Exception? conflictingWriteException;
        try
        {
            Assert.True(
                blocker.CallbackEntered.Wait(TimeSpan.FromSeconds(10)),
                "retained child callback did not reach the blocking handler");
            conflictingWriteException = Record.Exception(() => child.Name = "external");
        }
        finally
        {
            blocker.ContinueCallback.Set();
        }

        var edgeAdditionException = await edgeAddition.WaitAsync(TimeSpan.FromSeconds(10));
        var retryException = Record.Exception(() => child.Name = "after publication");

        // Assert
        Assert.IsType<LifecycleConflictException>(conflictingWriteException);
        Assert.Null(edgeAdditionException);
        Assert.Null(retryException);
        Assert.Equal("after publication", child.Name);
    }

    [Fact]
    public void WhenRetainedChildCallbackWritesScalar_ThenOwnerThreadWriteIsAllowed()
    {
        // Arrange
        var handler = new RetainedEdgeScalarWriter();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<ILifecycleHandler>(handler);
        var firstParent = new OrderNode(context) { Name = "first parent" };
        var secondParent = new OrderNode(context) { Name = "second parent" };
        var child = new OrderNode { Name = "child" };
        firstParent.Child = child;
        handler.Arm(child, secondParent);

        // Act
        var edgeAdditionException = Record.Exception(() => secondParent.Child = child);

        // Assert
        Assert.Null(edgeAdditionException);
        Assert.True(handler.Written);
        Assert.Null(handler.WriteException);
        Assert.Equal("written from callback", child.Name);
    }

    [Fact]
    public async Task WhenPropertyAttachCallbacksArePending_ThenItsStructuralWriteRetriesAfterPublication()
    {
        // Arrange
        using var blocker = new BlockingPropertyAttachHandler();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<IPropertyLifecycleHandler>(blocker);
        var root = new OrderNode(context) { Name = "root" };
        var child = new OrderNode { Name = "child" };
        var registeredRoot = root.TryGetRegisteredSubject()!;
        OrderNode? storedChild = null;
        blocker.Arm(root, "DynamicChild");

        // Act
        var propertyAdmission = Task.Run(() => Record.Exception(() =>
        {
            registeredRoot.AddProperty<OrderNode?>(
                "DynamicChild",
                _ => Volatile.Read(ref storedChild),
                (_, value) => Volatile.Write(ref storedChild, value));
        }));
        Exception? conflictingWriteException;
        SubjectPropertyMetadata metadata;
        try
        {
            Assert.True(
                blocker.CallbackEntered.Wait(TimeSpan.FromSeconds(10)),
                "property attach callback did not reach the blocking handler");
            metadata = ((IInterceptorSubject)root).Properties["DynamicChild"];
            conflictingWriteException = Record.Exception(() => metadata.SetValue!(root, child));
        }
        finally
        {
            blocker.ContinueCallback.Set();
        }

        var propertyAdmissionException = await propertyAdmission.WaitAsync(TimeSpan.FromSeconds(10));
        var retryException = Record.Exception(() => metadata.SetValue!(root, child));

        // Assert
        Assert.IsType<LifecycleConflictException>(conflictingWriteException);
        Assert.Null(propertyAdmissionException);
        Assert.Null(retryException);
        var registeredChild = child.TryGetRegisteredSubject();
        var registeredProperty = root.TryGetRegisteredSubject()!.TryGetProperty("DynamicChild");
        Assert.NotNull(registeredChild);
        Assert.NotNull(registeredProperty);
        Assert.Same(child, Assert.Single(registeredProperty.Children).Subject);
        Assert.Same(registeredProperty, Assert.Single(registeredChild.Parents).Property);
    }

    [Fact]
    public async Task WhenRetainedChildLosesOlderParentBeforeAdmittedPropertyCallbacks_ThenRegistrySettlesCausally()
    {
        // Arrange
        using var blocker = new BlockingPropertyAttachHandler();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<IPropertyLifecycleHandler>(blocker);
        var oldParent = new OrderNode(context) { Name = "old parent" };
        var newParent = new OrderNode(context) { Name = "new parent" };
        var child = new OrderNode { Name = "child" };
        oldParent.Child = child;
        OrderNode? storedChild = child;
        blocker.Arm(newParent, "DynamicChild");

        // Act
        var propertyAdmission = Task.Run(() => Record.Exception(() =>
        {
            newParent.TryGetRegisteredSubject()!.AddProperty<OrderNode?>(
                "DynamicChild",
                _ => Volatile.Read(ref storedChild),
                (_, value) => Volatile.Write(ref storedChild, value));
        }));
        Exception? removalException;
        try
        {
            Assert.True(
                blocker.CallbackEntered.Wait(TimeSpan.FromSeconds(10)),
                "property attach callback did not reach the blocking handler");
            removalException = Record.Exception(() => oldParent.Child = null);
        }
        finally
        {
            blocker.ContinueCallback.Set();
        }

        var propertyAdmissionException = await propertyAdmission.WaitAsync(TimeSpan.FromSeconds(10));
        var retryException = Record.Exception(() => oldParent.Child = null);

        // Assert
        Assert.IsType<LifecycleConflictException>(removalException);
        Assert.Null(propertyAdmissionException);
        Assert.Null(retryException);
        var registeredProperty = newParent.TryGetRegisteredSubject()!.TryGetProperty("DynamicChild");
        var registeredChild = child.TryGetRegisteredSubject();
        Assert.NotNull(registeredProperty);
        Assert.NotNull(registeredChild);
        Assert.Same(child, Assert.Single(registeredProperty.Children).Subject);
        Assert.Same(registeredProperty, Assert.Single(registeredChild.Parents).Property);
    }

    [Fact]
    public async Task WhenBeforeRegistryAttachCallbackAddsProperty_ThenOuterFenceCoversNestedAdmission()
    {
        // Arrange
        using var handler = new NestedAdmissionBeforeRegistryHandler();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<ILifecycleHandler>(handler);
        var child = new OrderNode { Name = "child" };

        // Act
        var rootAttach = Task.Run(() => Record.Exception(() => _ = new OrderNode(context)));
        Exception? conflictingWriteException;
        try
        {
            Assert.True(
                handler.NestedAdmissionCompleted.Wait(TimeSpan.FromSeconds(10)),
                "nested property admission did not complete before Registry attachment");
            var root = Assert.IsType<OrderNode>(handler.Subject);
            var metadata = ((IInterceptorSubject)root).Properties[NestedAdmissionBeforeRegistryHandler.PropertyName];
            conflictingWriteException = Record.Exception(() => metadata.SetValue!(root, child));
        }
        finally
        {
            handler.ContinueOuterCallback.Set();
        }

        var rootAttachException = await rootAttach.WaitAsync(TimeSpan.FromSeconds(10));
        var attachedRoot = Assert.IsType<OrderNode>(handler.Subject);

        // Assert
        Assert.IsType<LifecycleConflictException>(conflictingWriteException);
        Assert.Null(rootAttachException);
        Assert.NotNull(attachedRoot.TryGetRegisteredSubject()!
            .TryGetProperty(NestedAdmissionBeforeRegistryHandler.PropertyName));
    }

    [Fact]
    public void WhenNestedPropertyInitializerRunsBeforeOuterRegistryAttach_ThenFullProjectionIsVisible()
    {
        // Arrange: attaching the child publishes its graph parent before callbacks. A handler ahead
        // of Registry admits a dynamic property, whose Registry callback and initializer therefore
        // run before Registry receives the child's outer context-attach callback.
        var handler = new NestedProjectionBeforeRegistryHandler();
        var initializer = new NestedProjectionInitializer();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<ILifecycleHandler>(handler);
        context.AddService<ISubjectPropertyInitializer>(initializer);
        var root = new OrderNode(context) { Name = "root" };
        var child = new OrderNode { Name = "child" };
        handler.Arm(child);
        initializer.Arm(child, root);

        // Act
        root.Child = child;

        // Assert
        Assert.True(handler.Admitted);
        Assert.True(initializer.Invoked);
        Assert.True(initializer.SiblingVisible);
        Assert.True(initializer.RegistryParentVisible);
    }

    [Fact]
    public void WhenNestedSelfEdgeAdvancesProjectionBeforeOuterRegistryAttach_ThenSubjectIdIsRegistered()
    {
        // Arrange: the property's callback creates the provisional Registry subject, then its
        // self-edge callback advances that subject's projection revision before the older outer
        // context-attach callback reaches Registry.
        var handler = new NestedSelfEdgeBeforeRegistryHandler();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<ILifecycleHandler>(handler);
        var subject = new OrderNode { Name = "subject" };
        ((IInterceptorSubject)subject).SetSubjectId("nested-self-edge");
        handler.Arm(subject);

        // Act
        ((IInterceptorSubject)subject).AttachToContext(context);

        // Assert
        Assert.True(handler.Admitted);
        var registry = context.GetService<ISubjectIdRegistry>();
        Assert.True(registry.TryGetSubjectById("nested-self-edge", out var registered));
        Assert.Same(subject, registered);
    }

    [Fact]
    public void WhenNestedParentEdgeAdvancesProjectionBeforeOuterRegistryAttach_ThenChildStillRegisters()
    {
        // Arrange: unlike the self-edge case, the nested property belongs to an already registered
        // parent. Only its newer edge callback targets the provisionally attaching child before the
        // child's older outer context-attach callback reaches Registry.
        var handler = new NestedParentEdgeBeforeRegistryHandler();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<ILifecycleHandler>(handler);
        var root = new OrderNode(context) { Name = "root" };
        var child = new OrderNode { Name = "child" };
        handler.Arm(root, child);

        // Act
        ((IInterceptorSubject)child).AttachToContext(
            context, SubjectAttachmentAnchorKind.Provisional);

        // Assert
        Assert.True(handler.Admitted);
        var registeredChild = child.TryGetRegisteredSubject();
        Assert.NotNull(registeredChild);
        Assert.Single(registeredChild.Parents);
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class FirstHandlerParentProbe : ILifecycleHandler
    {
        public List<bool> EdgeVisibleInParents { get; } = [];

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change is { IsPropertyReferenceAdded: true, Property: { } property })
            {
                EdgeVisibleInParents.Add(change.Subject.GetParents()
                    .Any(parent => parent.Property.Equals(property) && Equals(parent.Index, change.Index)));
            }
        }
    }

    private sealed class RegistryLockProbeInitializer : ISubjectPropertyInitializer
    {
        private IInterceptorSubject? _subject;
        private ISubjectRegistry? _registry;
        private int _probed;

        internal bool CallbackReached { get; private set; }
        internal Exception? WorkerException { get; private set; }

        internal void Arm(IInterceptorSubject subject, ISubjectRegistry registry)
        {
            _subject = subject;
            _registry = registry;
        }

        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            if (!ReferenceEquals(property.Subject, _subject) || Interlocked.Exchange(ref _probed, 1) != 0)
            {
                return;
            }

            CallbackReached = true;
            var worker = new Thread(() =>
            {
                WorkerException = Record.Exception(() => { _registry!.TryGetRegisteredSubject(_subject!); });
            }) { IsBackground = true };
            worker.Start();
            if (!worker.Join(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("the Registry initializer ran while the Registry lock was held");
            }
        }
    }

    private sealed class RegistryLockProbeAttributes(
        ISubjectRegistry registry,
        IInterceptorSubject subject) : IReadOnlyCollection<Attribute>
    {
        private int _enumerationCount;

        public int Count => 0;

        internal int EnumerationCount => Volatile.Read(ref _enumerationCount);

        internal bool WorkerCompleted { get; private set; }

        internal Exception? WorkerException { get; private set; }

        public IEnumerator<Attribute> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerationCount) == 2)
            {
                var worker = new Thread(() =>
                {
                    WorkerException = Record.Exception(() => registry.TryGetRegisteredSubject(subject));
                }) { IsBackground = true };
                worker.Start();
                WorkerCompleted = worker.Join(TimeSpan.FromSeconds(5));
                if (!WorkerCompleted)
                {
                    throw new TimeoutException("attribute metadata was enumerated under the Registry lock");
                }
            }

            return Array.Empty<Attribute>().AsEnumerable().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class BlockingAttachHandler : ILifecycleHandler, IDisposable
    {
        private IInterceptorSubject? _target;

        internal ManualResetEventSlim CallbackEntered { get; } = new(false);
        internal ManualResetEventSlim ContinueCallback { get; } = new(false);

        internal void Arm(IInterceptorSubject target) => _target = target;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsContextAttach || !ReferenceEquals(change.Subject, _target))
            {
                return;
            }

            CallbackEntered.Set();
            if (!ContinueCallback.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("the test did not release the ancestor attach callback");
            }
        }

        public void Dispose()
        {
            CallbackEntered.Dispose();
            ContinueCallback.Dispose();
        }
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class BlockingPropertyAttachHandler : IPropertyLifecycleHandler, IDisposable
    {
        private IInterceptorSubject? _target;
        private string? _propertyName;

        internal ManualResetEventSlim CallbackEntered { get; } = new(false);
        internal ManualResetEventSlim ContinueCallback { get; } = new(false);

        internal void Arm(IInterceptorSubject target, string propertyName)
        {
            _target = target;
            _propertyName = propertyName;
        }

        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
            if (!ReferenceEquals(change.Subject, _target) || change.Property.Name != _propertyName)
            {
                return;
            }

            CallbackEntered.Set();
            if (!ContinueCallback.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("the test did not release the property attach callback");
            }
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
        }

        public void Dispose()
        {
            CallbackEntered.Dispose();
            ContinueCallback.Dispose();
        }
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class BlockingRetainedEdgeHandler : ILifecycleHandler, IDisposable
    {
        private IInterceptorSubject? _target;
        private IInterceptorSubject? _expectedParent;

        internal ManualResetEventSlim CallbackEntered { get; } = new(false);
        internal ManualResetEventSlim ContinueCallback { get; } = new(false);

        internal void Arm(IInterceptorSubject target, IInterceptorSubject expectedParent)
        {
            _target = target;
            _expectedParent = expectedParent;
        }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsPropertyReferenceAdded || !ReferenceEquals(change.Subject, _target) ||
                change.Property is not { } property ||
                !ReferenceEquals(property.Subject, _expectedParent))
            {
                return;
            }

            CallbackEntered.Set();
            if (!ContinueCallback.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("the test did not release the retained child callback");
            }
        }

        public void Dispose()
        {
            CallbackEntered.Dispose();
            ContinueCallback.Dispose();
        }
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class RetainedEdgeScalarWriter : ILifecycleHandler
    {
        private OrderNode? _target;
        private IInterceptorSubject? _expectedParent;

        internal bool Written { get; private set; }
        internal Exception? WriteException { get; private set; }

        internal void Arm(OrderNode target, IInterceptorSubject expectedParent)
        {
            _target = target;
            _expectedParent = expectedParent;
        }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsPropertyReferenceAdded || !ReferenceEquals(change.Subject, _target) ||
                change.Property is not { } property ||
                !ReferenceEquals(property.Subject, _expectedParent))
            {
                return;
            }

            Written = true;
            WriteException = Record.Exception(() => _target!.Name = "written from callback");
        }
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class NestedAdmissionBeforeRegistryHandler : ILifecycleHandler, IDisposable
    {
        internal const string PropertyName = "NestedChild";

        private OrderNode? _storedChild;
        private int _handled;

        internal IInterceptorSubject? Subject { get; private set; }
        internal ManualResetEventSlim NestedAdmissionCompleted { get; } = new(false);
        internal ManualResetEventSlim ContinueOuterCallback { get; } = new(false);

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsContextAttach || Interlocked.Exchange(ref _handled, 1) != 0)
            {
                return;
            }

            Subject = change.Subject;
            change.Subject.AddProperties(new SubjectPropertyMetadata(
                PropertyName,
                typeof(OrderNode),
                [],
                _ => Volatile.Read(ref _storedChild),
                (subject, value) => ((InterceptorExecutor)subject.Executor).SetGeneratedPropertyValue(
                    PropertyName,
                    (OrderNode?)value,
                    _ => Volatile.Read(ref _storedChild),
                    (_, committed) => Volatile.Write(ref _storedChild, committed)),
                isIntercepted: true,
                isDynamic: true));

            NestedAdmissionCompleted.Set();
            if (!ContinueOuterCallback.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("the test did not release the outer attach callback");
            }
        }

        public void Dispose()
        {
            NestedAdmissionCompleted.Dispose();
            ContinueOuterCallback.Dispose();
        }
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class NestedProjectionBeforeRegistryHandler : ILifecycleHandler
    {
        internal const string PropertyName = "NestedProjectionProbe";

        private IInterceptorSubject? _target;
        private int _handled;

        internal bool Admitted { get; private set; }

        internal void Arm(IInterceptorSubject target) => _target = target;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsContextAttach || !ReferenceEquals(change.Subject, _target) ||
                Interlocked.Exchange(ref _handled, 1) != 0)
            {
                return;
            }

            change.Subject.AddProperties(new SubjectPropertyMetadata(
                PropertyName,
                typeof(string),
                [],
                _ => "probe",
                null,
                isIntercepted: true,
                isDynamic: true));
            Admitted = true;
        }
    }

    private sealed class NestedProjectionInitializer : ISubjectPropertyInitializer
    {
        private IInterceptorSubject? _target;
        private IInterceptorSubject? _expectedParent;

        internal bool Invoked { get; private set; }
        internal bool SiblingVisible { get; private set; }
        internal bool RegistryParentVisible { get; private set; }

        internal void Arm(IInterceptorSubject target, IInterceptorSubject expectedParent)
        {
            _target = target;
            _expectedParent = expectedParent;
        }

        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            if (!ReferenceEquals(property.Subject, _target) ||
                property.Name != NestedProjectionBeforeRegistryHandler.PropertyName)
            {
                return;
            }

            Invoked = true;
            SiblingVisible = property.Parent.TryGetProperty(nameof(OrderNode.Name)) is not null;
            RegistryParentVisible = property.Parent.Parents.Any(parent =>
                ReferenceEquals(parent.Property.Subject, _expectedParent) &&
                parent.Property.Name == nameof(OrderNode.Child));
        }
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class NestedSelfEdgeBeforeRegistryHandler : ILifecycleHandler
    {
        private IInterceptorSubject? _target;
        private int _handled;

        internal bool Admitted { get; private set; }

        internal void Arm(IInterceptorSubject target) => _target = target;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsContextAttach || !ReferenceEquals(change.Subject, _target) ||
                Interlocked.Exchange(ref _handled, 1) != 0)
            {
                return;
            }

            change.Subject.AddProperties(new SubjectPropertyMetadata(
                "NestedSelf",
                typeof(OrderNode),
                [],
                _ => change.Subject,
                null,
                isIntercepted: true,
                isDynamic: true));
            Admitted = true;
        }
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class NestedParentEdgeBeforeRegistryHandler : ILifecycleHandler
    {
        private IInterceptorSubject? _parent;
        private IInterceptorSubject? _target;
        private int _handled;

        internal bool Admitted { get; private set; }

        internal void Arm(IInterceptorSubject parent, IInterceptorSubject target)
        {
            _parent = parent;
            _target = target;
        }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsContextAttach || !ReferenceEquals(change.Subject, _target) ||
                Interlocked.Exchange(ref _handled, 1) != 0)
            {
                return;
            }

            _parent!.AddProperties(new SubjectPropertyMetadata(
                "NestedChildEdge",
                typeof(OrderNode),
                [],
                _ => _target,
                null,
                isIntercepted: true,
                isDynamic: true));
            Admitted = true;
        }
    }
}

[InterceptorSubject]
public partial class OrderNode : ILifecycleHandler
{
    public partial string Name { get; set; }

    public partial OrderNode? Child { get; set; }

    public string[] AncestorsVisibleDuringAttach { get; private set; } = [];

    public string[] AncestorsViaRegistryDuringAttach { get; private set; } = [];

    public string[] AncestorsVisibleDuringDetach { get; private set; } = [];

    public int ParentLinkCountDuringDetach { get; private set; } = -1;

    public OrderNode()
    {
        Name = string.Empty;
    }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            AncestorsVisibleDuringAttach = CollectOverParentLinks(this, []);
            AncestorsViaRegistryDuringAttach = CollectOverRegistryEdges(this, []);
        }
        else if (change.IsContextDetach)
        {
            ParentLinkCountDuringDetach = ((IInterceptorSubject)this).GetParents().Length;
            AncestorsVisibleDuringDetach = CollectOverParentLinks(this, []);
        }
    }

    private static string[] CollectOverParentLinks(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
    {
        if (!visited.Add(subject))
        {
            return [];
        }

        var ancestors = new List<string>();
        foreach (var parent in subject.GetParents())
        {
            var parentSubject = parent.Property.Subject;
            if (parentSubject.TryGetRegisteredSubject() is not null && parentSubject is OrderNode node)
            {
                ancestors.Add(node.Name);
            }

            ancestors.AddRange(CollectOverParentLinks(parentSubject, visited));
        }

        return ancestors.ToArray();
    }

    private static string[] CollectOverRegistryEdges(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
    {
        if (!visited.Add(subject) || subject.TryGetRegisteredSubject() is not { } registered)
        {
            return [];
        }

        var ancestors = new List<string>();
        foreach (var parent in registered.Parents)
        {
            var parentSubject = parent.Property.Parent.Subject;
            if (parentSubject is OrderNode node)
            {
                ancestors.Add(node.Name);
            }

            ancestors.AddRange(CollectOverRegistryEdges(parentSubject, visited));
        }

        return ancestors.ToArray();
    }
}

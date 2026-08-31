using Namotion.Interceptor.Attributes;
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
    public void WhenAnOlderPropertyProjectionDrainsLast_ThenItCannotOverwriteTheNewerProjection()
    {
        // Arrange: both writes retain the same subjects, so no attachment-revision shortcut can
        // suppress either callback. The first callback parks before Registry delivery, the second
        // restores the original order and drains, then the older callback resumes last.
        var blocker = new BlockingPropertyProjectionHandler();
        var context = InterceptorSubjectContext.Create();
        context.AddService<IPropertyLifecycleHandler>(blocker);
        context.WithRegistry();
        var root = new Person(context);
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        root.Children = [first, second];
        blocker.Arm(root, nameof(Person.Children));

        Exception? olderException = null;
        var olderWriter = new Thread(() =>
        {
            olderException = Record.Exception(() => { root.Children = [second, first]; });
        }) { IsBackground = true };

        // Act
        olderWriter.Start();
        if (!blocker.Entered.Wait(TimeSpan.FromSeconds(10)))
        {
            blocker.Release.Set();
            olderWriter.Join(TimeSpan.FromSeconds(10));
            Assert.Fail($"the older property projection did not reach its callback park: {olderException}");
        }
        var newerException = Record.Exception(() => { root.Children = [first, second]; });
        blocker.Release.Set();
        Assert.True(olderWriter.Join(TimeSpan.FromSeconds(10)),
            "the older property projection did not finish after release");

        // Assert
        Assert.Null(olderException);
        Assert.Null(newerException);
        var property = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Children))!;
        Assert.Collection(
            property.Children,
            child =>
            {
                Assert.Same(first, child.Subject);
                Assert.Equal(0, child.Index);
            },
            child =>
            {
                Assert.Same(second, child.Subject);
                Assert.Equal(1, child.Index);
            });
        Assert.Equal(0, Assert.Single(first.TryGetRegisteredSubject()!.Parents).Index);
        Assert.Equal(1, Assert.Single(second.TryGetRegisteredSubject()!.Parents).Index);
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

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class BlockingPropertyProjectionHandler : IPropertyLifecycleHandler
    {
        private IInterceptorSubject? _subject;
        private string? _propertyName;
        private int _blocked;

        internal ManualResetEventSlim Entered { get; } = new(false);
        internal ManualResetEventSlim Release { get; } = new(false);

        internal void Arm(IInterceptorSubject subject, string propertyName)
        {
            _subject = subject;
            _propertyName = propertyName;
        }

        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
        }

        public void RefreshCollectionProperty(SubjectPropertyLifecycleChange change)
        {
            if (!ReferenceEquals(change.Property.Subject, _subject) || change.Property.Name != _propertyName ||
                Interlocked.Exchange(ref _blocked, 1) != 0)
            {
                return;
            }

            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("the older Registry projection was not released");
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

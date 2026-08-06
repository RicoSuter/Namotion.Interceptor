using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Pins that <see cref="SubjectRegistry"/> resolves ahead of <see cref="ContextInheritanceHandler"/>
/// for every registration order, and pins what a subject can therefore see of its ancestors from its
/// own lifecycle callback. See "Handler Order Around the Descent" in docs/design/tracking-lifecycle.md.
///
/// Only the third order moves: without the attribute it resolves the registry behind the descent,
/// which leaves a hole in the middle of an attaching subject's ancestor chain.
/// </summary>
public class RegistryHandlerOrderTests
{
    public static TheoryData<string> RegistrationOrders() =>
    [
        "registry-first",
        "registry-after-tracking",
        "registry-after-parents"
    ];

    private static IInterceptorSubjectContext CreateContext(string registrationOrder)
    {
        var context = InterceptorSubjectContext.Create();
        return registrationOrder switch
        {
            "registry-first" => context.WithRegistry().WithParents().WithFullPropertyTracking(),
            "registry-after-tracking" => context.WithFullPropertyTracking().WithRegistry().WithParents(),
            "registry-after-parents" => context.WithFullPropertyTracking().WithParents().WithRegistry(),
            _ => throw new ArgumentOutOfRangeException(nameof(registrationOrder))
        };
    }

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

        // Assert: the whole sequence, not just registry-before-descent. Both recorders are ordered
        // against the descent and against each other, so composition order cannot reshuffle the
        // pre-descent segment and a handler placed inside it sees the same state either way.
        Assert.Equal(
            [nameof(SubjectRegistry), nameof(ParentTrackingHandler), nameof(ContextInheritanceHandler)],
            handlers);
    }

    [Fact]
    public void WhenParentTrackingIsAbsent_ThenRegistryParentEdgesResolveTheWholeChainDuringAttach()
    {
        // Arrange: tracking before registry with no parent tracking, which is the shape the README
        // and the connector docs use. GetParents() is empty without ParentTrackingHandler, so the
        // ancestor chain has to be walked over the registry's own parent edges instead.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out var child);

        // Act
        root.Child = top;

        // Assert
        Assert.Equal(["middle", "top", "root"], child.AncestorsViaRegistryDuringAttach);
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

        // Assert: "middle" is the entry a registry behind the descent would be missing. The child's
        // own registration pulls in its immediate parent on demand and the root attached earlier, so
        // the gap falls in the middle of the chain rather than truncating it.
        Assert.Equal(["middle", "top", "root"], child.AncestorsVisibleDuringAttach);
    }

    [Theory]
    [MemberData(nameof(RegistrationOrders))]
    public void WhenSubtreeIsDetached_ThenAncestorsAreAlreadyDeregisteredButParentLinkRemains(string registrationOrder)
    {
        // Arrange
        var context = CreateContext(registrationOrder);
        var root = new OrderNode(context) { Name = "root" };
        var top = BuildDetachedSubtree(out var child);
        root.Child = top;

        // Act
        root.Child = null;

        // Assert: detach is deliberately not the mirror of attach. A subject's own handler runs
        // before the context handlers that deregister it, but its ancestors are processed further up
        // the descent and have already been deregistered by the time the callback reaches this
        // subject. The parent link outlives the registry entry, because ParentTrackingHandler clears
        // it for this subject only after this callback returns. Consumers that need ancestor state
        // while detaching must therefore use GetParents() or state captured at attach time.
        Assert.Equal(1, child.ParentLinkCountDuringDetach);
        Assert.Empty(child.AncestorsVisibleDuringDetach);
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
            AncestorsVisibleDuringAttach = CollectRegistryVisibleAncestors(this, []);
            AncestorsViaRegistryDuringAttach = CollectAncestorsOverRegistryEdges(this, []);
        }
        else if (change.IsContextDetach)
        {
            ParentLinkCountDuringDetach = ((IInterceptorSubject)this).GetParents().Length;
            AncestorsVisibleDuringDetach = CollectRegistryVisibleAncestors(this, []);
        }
    }

    /// <summary>
    /// Walks the registry's own parent edges rather than <see cref="ParentsHandlerExtensions.GetParents"/>,
    /// so the chain is observable on a context that does not register parent tracking.
    /// </summary>
    private static string[] CollectAncestorsOverRegistryEdges(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
    {
        if (!visited.Add(subject))
        {
            return [];
        }

        var registered = subject.TryGetRegisteredSubject();
        if (registered is null)
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

            ancestors.AddRange(CollectAncestorsOverRegistryEdges(parentSubject, visited));
        }

        return ancestors.ToArray();
    }

    private static string[] CollectRegistryVisibleAncestors(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
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

            ancestors.AddRange(CollectRegistryVisibleAncestors(parentSubject, visited));
        }

        return ancestors.ToArray();
    }
}

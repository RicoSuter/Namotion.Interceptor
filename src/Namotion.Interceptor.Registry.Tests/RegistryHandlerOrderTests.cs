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
    public void WhenRegistryIsRegisteredInAnyOrder_ThenItResolvesAheadOfContextInheritance(string registrationOrder)
    {
        // Arrange
        var context = CreateContext(registrationOrder);

        // Act
        var handlers = context.GetServices<ILifecycleHandler>()
            .Select(handler => handler.GetType().Name)
            .ToArray();

        // Assert
        Assert.True(
            Array.IndexOf(handlers, nameof(SubjectRegistry)) < Array.IndexOf(handlers, nameof(ContextInheritanceHandler)),
            $"Expected the registry ahead of the descent but resolved: {string.Join(" -> ", handlers)}");
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
        // it for this subject only after this callback returns. Both production chains
        // (HomeBlaze SubjectContextFactory, variables2 ServiceConfiguration) already resolve the
        // registry ahead, so this is the order they see today; the attribute makes it uniform.
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
        }
        else if (change.IsContextDetach)
        {
            ParentLinkCountDuringDetach = ((IInterceptorSubject)this).GetParents().Length;
            AncestorsVisibleDuringDetach = CollectRegistryVisibleAncestors(this, []);
        }
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

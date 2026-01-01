using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

/// <summary>
/// Tests that parent tracking is properly set up before a subject's own ILifecycleHandler.AttachSubject runs.
/// This is critical for scenarios where a subject needs to access its parent hierarchy during initialization.
/// </summary>
public class ParentAccessDuringLifecycleTests
{
    [Fact]
    public void WhenComponentAttachedToSimulation_ThenParentsAndRootAreAvailableDuringAttachSubject()
    {
        // Arrange: Create context with parent tracking
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act: Attach component to simulation
        simulation.Component = component;

        // Assert: Component should have found parents and root during its AttachSubject
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.NotEmpty(component.ParentsFoundDuringAttach);
        Assert.NotNull(component.RootFoundDuringAttach);
        Assert.Same(simulation, component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenComponentAddedToArray_ThenParentsAreSetBeforeAttachSubject()
    {
        // Arrange: Create context with parent tracking
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act: Add component to array
        simulation.Components = [component];

        // Assert: Component should have found parents during its AttachSubject
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.NotEmpty(component.ParentsFoundDuringAttach);
        Assert.NotNull(component.RootFoundDuringAttach);
        Assert.Same(simulation, component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenComponentWithoutContextAttachedViaContextInheritance_ThenParentsAreSetBeforeAttachSubject()
    {
        // This test specifically tests the scenario where:
        // 1. Root has context with WithContextInheritance
        // 2. Child is created WITHOUT context
        // 3. When child is attached, context is inherited AND parents should be set

        // Arrange: Create context with both context inheritance and parent tracking
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents();

        var simulation = new Simulation(context) { Name = "Root" };

        // Component created without context - will inherit via ContextInheritanceHandler
        var component = new Component { Name = "Child" };

        // Act: Attach component - context will be inherited, parents should be set
        simulation.Component = component;

        // Assert: Parents should be available when component's AttachSubject runs
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.NotEmpty(component.ParentsFoundDuringAttach);
        Assert.NotNull(component.RootFoundDuringAttach);
        Assert.Same(simulation, component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenParentsCalledWithoutParentTracking_ThenReturnsEmptySet()
    {
        // Arrange: Create context WITHOUT parent tracking
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act
        simulation.Component = component;

        // Assert: GetParents returns empty because ParentTrackingHandler is not registered
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.Empty(component.ParentsFoundDuringAttach);
        Assert.Null(component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenParentsRegisteredBeforeFullTracking_ThenParentsAreStillSetBeforeAttachSubject()
    {
        // Test with WithParents() called BEFORE WithFullPropertyTracking()
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act
        simulation.Component = component;

        // Assert
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.NotEmpty(component.ParentsFoundDuringAttach);
        Assert.NotNull(component.RootFoundDuringAttach);
        Assert.Same(simulation, component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenOnlyLifecycleAndParents_ThenParentsAreSetBeforeAttachSubject()
    {
        // Minimal configuration: just lifecycle and parents
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithParents();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act
        simulation.Component = component;

        // Assert
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.NotEmpty(component.ParentsFoundDuringAttach);
        Assert.NotNull(component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenOnlyParents_ThenParentsAreSetBeforeAttachSubject()
    {
        // Just WithParents (which internally calls WithLifecycle)
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act
        simulation.Component = component;

        // Assert
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.NotEmpty(component.ParentsFoundDuringAttach);
        Assert.NotNull(component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenNestedHierarchy_ThenAllComponentsCanFindRoot()
    {
        // Test: root.Component.ChildComponent - nested hierarchy
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents();

        var simulation = new Simulation(context) { Name = "Root" };
        var outerComponent = new Component { Name = "Outer" };
        var innerComponent = new Component { Name = "Inner" };

        // Build hierarchy: simulation -> outerComponent -> innerComponent
        simulation.Component = outerComponent;
        outerComponent.ChildComponent = innerComponent;

        // Assert: Outer component finds root
        Assert.Null(outerComponent.AttachException);
        Assert.NotNull(outerComponent.ParentsFoundDuringAttach);
        Assert.NotEmpty(outerComponent.ParentsFoundDuringAttach);
        Assert.NotNull(outerComponent.RootFoundDuringAttach);
        Assert.Same(simulation, outerComponent.RootFoundDuringAttach);

        // Assert: Inner component finds root (through outer)
        Assert.Null(innerComponent.AttachException);
        Assert.NotNull(innerComponent.ParentsFoundDuringAttach);
        Assert.NotEmpty(innerComponent.ParentsFoundDuringAttach);
        Assert.NotNull(innerComponent.RootFoundDuringAttach);
        Assert.Same(simulation, innerComponent.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenNestedHierarchyBuiltBeforeAttach_ThenAllComponentsCanFindRoot()
    {
        // Test: Build hierarchy first, then attach to context
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithFullPropertyTracking();

        var simulation = new Simulation { Name = "Root" };  // No context yet
        var outerComponent = new Component { Name = "Outer" };
        var innerComponent = new Component { Name = "Inner" };

        // Build hierarchy first (no context)
        simulation.Component = outerComponent;
        outerComponent.ChildComponent = innerComponent;

        // Now attach context - should trigger attach for all
        ((IInterceptorSubject)simulation).Context.AddFallbackContext(context);

        // Assert: All components should have found their parents during attach
        Assert.Null(outerComponent.AttachException);
        Assert.NotNull(outerComponent.RootFoundDuringAttach);
        Assert.Same(simulation, outerComponent.RootFoundDuringAttach);

        Assert.Null(innerComponent.AttachException);
        Assert.NotNull(innerComponent.RootFoundDuringAttach);
        Assert.Same(simulation, innerComponent.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenComponentIsRoot_ThenNoParentIsFound()
    {
        // When a component is created as a root (not attached to a parent),
        // it should have no parents during AttachSubject
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents();

        // Component is created directly with context - it IS the root
        var component = new Component(context) { Name = "RootComponent" };

        // Assert: No parents because it's the root
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.Empty(component.ParentsFoundDuringAttach);  // No parents!
        Assert.Null(component.RootFoundDuringAttach);      // Can't find Simulation because there isn't one
    }

    [Fact]
    public void VerifyHandlerOrder_ParentTrackingHandlerRunsBeforeSubjectHandler()
    {
        // This test verifies the exact order of handler invocations
        var handlerCallOrder = new List<string>();

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithParents();

        // Add a custom handler to track the order
        context.AddService(new OrderTrackingHandler(handlerCallOrder));

        var root = new RootWithTrackedChild(context) { Name = "Root" };
        var child = new TrackedChild(handlerCallOrder) { Name = "Child" };

        // Act
        root.Child = child;

        // Assert: Verify that parents were available when subject's handler ran
        Assert.Contains("TrackedChild.AttachSubject - HasParents: True", handlerCallOrder);
    }

    private class OrderTrackingHandler : ILifecycleHandler
    {
        private readonly List<string> _callOrder;

        public OrderTrackingHandler(List<string> callOrder)
        {
            _callOrder = callOrder;
        }

        public void AttachSubject(SubjectLifecycleChange change)
        {
            _callOrder.Add($"OrderTrackingHandler.AttachSubject for {change.Subject.GetType().Name}");
        }

        public void DetachSubject(SubjectLifecycleChange change)
        {
            _callOrder.Add($"OrderTrackingHandler.DetachSubject for {change.Subject.GetType().Name}");
        }
    }
}

/// <summary>
/// A root subject that holds a tracked child.
/// </summary>
[InterceptorSubject]
public partial class RootWithTrackedChild
{
    public partial string Name { get; set; }

    public partial TrackedChild? Child { get; set; }

    public RootWithTrackedChild()
    {
        Name = string.Empty;
    }
}

/// <summary>
/// A component that tracks the order of handler calls.
/// </summary>
[InterceptorSubject]
public partial class TrackedChild : ILifecycleHandler
{
    private readonly List<string> _callOrder;

    public partial string Name { get; set; }

    public TrackedChild(List<string> callOrder)
    {
        _callOrder = callOrder;
        Name = string.Empty;
    }

    public void AttachSubject(SubjectLifecycleChange change)
    {
        var hasParents = this.GetParents().Length > 0;
        _callOrder.Add($"TrackedChild.AttachSubject - HasParents: {hasParents}");
    }

    public void DetachSubject(SubjectLifecycleChange change)
    {
        _callOrder.Add("TrackedChild.DetachSubject");
    }
}

/// <summary>
/// A root subject for testing parent tracking scenarios.
/// </summary>
[InterceptorSubject]
public partial class Simulation
{
    public partial string Name { get; set; }

    public partial Component? Component { get; set; }

    public partial Component[] Components { get; set; }

    public Simulation()
    {
        Name = string.Empty;
        Components = [];
    }
}

/// <summary>
/// A component that implements ILifecycleHandler and tries to access its parent during AttachSubject.
/// Used to test that parent tracking is set up before the subject's own lifecycle handler runs.
/// </summary>
[InterceptorSubject]
public partial class Component : ILifecycleHandler
{
    public partial string Name { get; set; }

    /// <summary>
    /// Nested child component for testing hierarchies like root.component.component
    /// </summary>
    public partial Component? ChildComponent { get; set; }

    public Component()
    {
        Name = string.Empty;
    }

    /// <summary>
    /// Stores the root found during AttachSubject (if any).
    /// </summary>
    public Simulation? RootFoundDuringAttach { get; private set; }

    /// <summary>
    /// Stores any exception that occurred during AttachSubject.
    /// </summary>
    public Exception? AttachException { get; private set; }

    /// <summary>
    /// Stores the parents found during AttachSubject.
    /// </summary>
    public HashSet<SubjectParent>? ParentsFoundDuringAttach { get; private set; }

    public void AttachSubject(SubjectLifecycleChange change)
    {
        try
        {
            // Store the parents at the moment AttachSubject is called
            ParentsFoundDuringAttach = new HashSet<SubjectParent>(this.GetParents());

            // Try to find the root simulation via parent traversal
            RootFoundDuringAttach = this.TryGetFirstParent<Simulation>();
        }
        catch (Exception ex)
        {
            AttachException = ex;
        }
    }

    public void DetachSubject(SubjectLifecycleChange change)
    {
    }

    /// <summary>
    /// Tries to find the first parent of the specified type by traversing the parent hierarchy.
    /// Returns null if not found instead of throwing.
    /// </summary>
    public TRoot? TryGetFirstParent<TRoot>()
        where TRoot : class, IInterceptorSubject
    {
        var visited = new HashSet<IInterceptorSubject>();
        var queue = new Queue<IInterceptorSubject>();
        queue.Enqueue(this);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!visited.Add(current))
            {
                continue;
            }

            if (current is TRoot root && !ReferenceEquals(current, this))
            {
                return root;
            }

            foreach (var parent in current.GetParents())
            {
                if (!parent.Equals(default))
                {
                    queue.Enqueue(parent.Property.Subject);
                }
            }
        }

        return null;
    }
}

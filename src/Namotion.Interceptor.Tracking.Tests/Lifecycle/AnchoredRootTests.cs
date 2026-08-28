using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Covers the predicate consumers need instead of reading an empty parent list as root-ness, which
/// cannot separate a genuine root from an unattached subject or from one inside its own release.
/// </summary>
public class AnchoredRootTests
{
    [Fact]
    public void WhenASubjectIsUnattached_ThenItIsNotAnAnchoredRoot()
    {
        // Arrange
        var subject = new Simulation { Name = "Detached" };

        // Act & Assert
        Assert.False(subject.IsAnchoredRoot());
    }

    [Fact]
    public void WhenASubjectIsConstructedWithAContext_ThenItIsAnAnchoredRoot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

        // Act
        var subject = new Simulation(context) { Name = "Root" };

        // Assert
        Assert.True(subject.IsAnchoredRoot());
    }

    [Fact]
    public void WhenASubjectIsHeldOnlyByAStructuralEdge_ThenItIsNotAnAnchoredRoot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act
        simulation.Component = component;

        // Assert
        Assert.True(simulation.IsAnchoredRoot());
        Assert.False(component.IsAnchoredRoot());
    }

    [Fact]
    public void WhenAReleasedChildIsObservedFromItsDetachCallback_ThenItIsNotAnAnchoredRoot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };
        simulation.Component = component;

        bool? anchoredDuringDetach = null;
        context.AddService(new DetachProbe(change =>
        {
            if (ReferenceEquals(change.Subject, component) && !change.IsContextAttach)
            {
                anchoredDuringDetach = change.Subject.IsAnchoredRoot();
            }
        }));

        // Act
        simulation.Component = null;

        // Assert: ownership is dropped before any detach callback runs, but the anchor is what the
        // predicate reads, and an edge-held child never had one.
        Assert.False(anchoredDuringDetach);
    }

    [Fact]
    public void WhenAnExplicitRootIsObservedFromItsDetachCallback_ThenItIsNotAnAnchoredRoot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation { Name = "Root" };
        simulation.AttachToContext(context);

        bool? anchoredDuringDetach = null;
        context.AddService(new DetachProbe(change =>
        {
            if (ReferenceEquals(change.Subject, simulation) && !change.IsContextAttach)
            {
                anchoredDuringDetach = change.Subject.IsAnchoredRoot();
            }
        }));

        // Act
        simulation.DetachFromContext(context);

        // Assert: the detach clears the anchor before releasing, so a handler never sees a
        // departing root reported as one, and it stays false once the detach returns.
        Assert.False(anchoredDuringDetach);
        Assert.False(simulation.IsAnchoredRoot());
    }

    [Fact]
    public void WhenAProvisionalRootGainsASupportingEdge_ThenItStopsBeingAnAnchoredRoot()
    {
        // Arrange: a context-constructed subject is a provisional root until a graph adopts it
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component(context) { Name = "Child" };
        Assert.True(component.IsAnchoredRoot());

        // Act
        simulation.Component = component;

        // Assert: adoption consumes the provisional anchor, so the value flips with no explicit call
        Assert.False(component.IsAnchoredRoot());
        Assert.True(simulation.IsAnchoredRoot());
    }

    [Fact]
    public void WhenAnInheritedSubjectIsPromotedToAnExplicitRoot_ThenItIsBothAnchoredAndReferenced()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };
        simulation.Component = component;

        // Act
        component.AttachToContext(context);

        // Assert: anchored does not mean parentless, so a consumer must not read root-ness as
        // "has no parents"
        Assert.True(component.IsAnchoredRoot());
        Assert.NotEmpty(component.GetParents());
    }

    [Fact]
    public void WhenABackEdgeFromItsOwnSubtreePointsAtAProvisionalRoot_ThenItStaysAnAnchoredRoot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Component(context) { Name = "Root" };
        var child = new Component { Name = "Child" };
        root.ChildComponent = child;

        // Act: a cycle back to the root, which is an edge but not a supporting one
        child.ChildComponent = root;

        // Assert: clearing on any incoming edge would be unsound, because the only thing holding
        // this component is the anchor a back edge from its own subtree cannot replace
        Assert.True(root.IsAnchoredRoot());
        Assert.False(child.IsAnchoredRoot());
    }

    private sealed class DetachProbe(Action<SubjectLifecycleChange> onChange) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change) => onChange(change);
    }
}

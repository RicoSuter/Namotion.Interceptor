using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

/// <summary>
/// Covers what the parent projection and the reference count answer when the lifecycle that
/// publishes them is absent, which is a different question from a subject genuinely having no
/// parents.
/// </summary>
public class ParentAvailabilityTests
{
    [Fact]
    public void WhenSubjectIsUnattached_ThenGetParentsReturnsEmpty()
    {
        // Arrange
        var subject = new Simulation { Name = "Detached" };

        // Act
        var parents = subject.GetParents();

        // Assert: no edge can point at an unattached subject, so empty is the answer itself
        Assert.Empty(parents);
    }

    [Fact]
    public void WhenSubjectIsUnattached_ThenGetReferenceCountReturnsZero()
    {
        // Arrange
        var subject = new Simulation { Name = "Detached" };

        // Act
        var referenceCount = subject.GetReferenceCount();

        // Assert
        Assert.Equal(0, referenceCount);
    }

    [Fact]
    public void WhenAttachedToAContextWithoutLifecycle_ThenGetParentsThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new Simulation(context) { Name = "Root" };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => subject.GetParents());
        Assert.Contains("WithLifecycle()", exception.Message);
    }

    [Fact]
    public void WhenAttachedToAContextWithoutLifecycle_ThenGetReferenceCountThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new Simulation(context) { Name = "Root" };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => subject.GetReferenceCount());
        Assert.Contains("WithLifecycle()", exception.Message);
    }

    [Fact]
    public void WhenLifecycleIsRegisteredWithoutRegistry_ThenParentsAreStillAvailable()
    {
        // Arrange: parents come from the lifecycle, so the registry is not required
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act
        simulation.Component = component;

        // Assert
        Assert.Single(component.GetParents());
        Assert.Equal(1, component.GetReferenceCount());
    }
}

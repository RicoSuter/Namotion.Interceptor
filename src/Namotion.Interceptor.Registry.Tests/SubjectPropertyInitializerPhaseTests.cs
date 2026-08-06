using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Attaching a subject runs two phases, and the boundary between them is invisible from a consumer's
/// side because both happen "during attach":
///
/// 1. <see cref="ILifecycleHandler"/> for the subject, from <c>LifecycleInterceptor</c>.
/// 2. Then the subject's properties attach, which is where <c>SubjectRegistry</c> invokes every
///    <see cref="ISubjectPropertyInitializer"/>.
///
/// So a lifecycle handler cannot see properties an initializer adds to the same subject, whatever
/// its ordering position, because both attach paths run the handler chain to completion before the
/// property loop. A handler that needs initializer output has to observe phase 2 instead.
///
/// The raw phase ordering is already pinned by the snapshot in
/// <c>LifecycleInterceptorTests.WhenSubjectIsAttachedThenAllPropertiesAreAttachedAndSameWithDetach</c>.
/// What these tests add is the consequence for <see cref="ISubjectPropertyInitializer"/>, which is
/// the form a consumer actually meets it in.
/// </summary>
public class SubjectPropertyInitializerPhaseTests
{
    private const string MarkerAttribute = "Marker";

    private static IInterceptorSubjectContext CreateContext(params object[] servicesAfterRegistry)
    {
        IInterceptorSubjectContext context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents();

        // Registered after WithRegistry so they resolve behind SubjectRegistry. For the property
        // phase observer that is load bearing: ahead of the registry it would run before the
        // initializer and observe the same absence for an unrelated reason.
        foreach (var service in servicesAfterRegistry)
        {
            context.AddService(service);
        }

        return context;
    }

    private static bool IsMarkerVisible(IInterceptorSubject subject)
    {
        return subject.TryGetRegisteredSubject()?
            .TryGetProperty(nameof(PhaseNode.Name))?
            .TryGetAttribute(MarkerAttribute) is not null;
    }

    [Fact]
    public void WhenALifecycleHandlerRunsDuringAttach_ThenAttributesFromAPropertyInitializerAreNotVisibleYet()
    {
        // Arrange
        var observations = new List<string>();
        var context = CreateContext(new MarkerInitializer(), new LifecyclePhaseObserver(observations));
        var root = new PhaseNode(context) { Name = "root" };
        var child = new PhaseNode { Name = "child" };
        observations.Clear();

        // Act
        root.Child = child;

        // Assert: registered=True is what makes this meaningful. The marker lookup goes through the
        // registry, so it also returns false for a subject that is merely not registered yet, and
        // without pinning registration the test would pass for that unrelated reason.
        // A single observation because the child is fresh, detached, and behind a single-valued
        // property; a collection or a pre-attached child would produce a different shape.
        Assert.Equal(["child:registered=True:marker=False"], observations);
    }

    [Fact]
    public void WhenAttachHasCompleted_ThenAttributesFromAPropertyInitializerAreVisible()
    {
        // Arrange
        var context = CreateContext(new MarkerInitializer());
        var root = new PhaseNode(context) { Name = "root" };
        var child = new PhaseNode { Name = "child" };

        // Act
        root.Child = child;

        // Assert: the same lookup that failed inside the handler succeeds once attach settles, so
        // the first test pins a timing gap rather than a missing attribute.
        Assert.True(IsMarkerVisible(child));
    }

    [Fact]
    public void WhenAPropertyLifecycleHandlerRunsBehindTheRegistry_ThenItSeesAttributesFromAPropertyInitializer()
    {
        // Arrange
        var observations = new List<string>();
        var context = CreateContext(new MarkerInitializer(), new PropertyPhaseObserver(observations));
        var root = new PhaseNode(context) { Name = "root" };
        var child = new PhaseNode { Name = "child" };
        observations.Clear();

        // Act
        root.Child = child;

        // Assert: this is the route out of the gap the first test pins. The assertion is on the
        // marker being visible, not merely on the property being observed, because a property phase
        // handler sees every property whether or not an initializer is registered at all.
        Assert.Contains($"{nameof(PhaseNode.Name)}:marker=True", observations);
    }

    private sealed class MarkerInitializer : ISubjectPropertyInitializer
    {
        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            // The guard is required, not defensive: a detach followed by a re-attach rebuilds the
            // RegisteredSubject from the subject's properties, which still carry the attribute added
            // the first time, and adding it again throws on the duplicate key.
            if (property.IsAttribute
                || property.Name != nameof(PhaseNode.Name)
                || property.TryGetAttribute(MarkerAttribute) is not null)
            {
                return;
            }

            property.AddAttribute(MarkerAttribute, typeof(string), _ => "set", null);
        }
    }

    private sealed class LifecyclePhaseObserver(List<string> log) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach && change.Subject is PhaseNode node)
            {
                var registered = change.Subject.TryGetRegisteredSubject() is not null;
                log.Add($"{node.Name}:registered={registered}:marker={IsMarkerVisible(change.Subject)}");
            }
        }
    }

    private sealed class PropertyPhaseObserver(List<string> log) : IPropertyLifecycleHandler
    {
        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
            if (change.Property.Subject is PhaseNode { Name: "child" })
            {
                log.Add($"{change.Property.Name}:marker={IsMarkerVisible(change.Property.Subject)}");
            }
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
        }

        public void RefreshCollectionProperty(PropertyReference property, object? value)
        {
        }
    }
}

[InterceptorSubject]
public partial class PhaseNode
{
    public partial string Name { get; set; }

    public partial PhaseNode? Child { get; set; }

    public PhaseNode()
    {
        Name = string.Empty;
    }
}

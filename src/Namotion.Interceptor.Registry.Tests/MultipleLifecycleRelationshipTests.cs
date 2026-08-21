using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

public class MultipleLifecycleRelationshipTests
{
    [Fact]
    public void WhenNoLifecycleServiceIsConfigured_ThenEqualStructuralAssignmentRemainsValid()
    {
        // Resolving structural refresh as a required unique lifecycle service would make the valid zero-service
        // configuration throw before preserving equality suppression.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithEqualityCheck();
        var parent = new MultipleLifecycleContainer(context);
        var child = new Person { FirstName = "Child" };
        var items = parent.Items;
        items.Add(child);

        // Act
        var exception = Record.Exception(() => parent.Items = items);

        // Assert
        Assert.Null(exception);
        Assert.Same(items, parent.Items);
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenOnlyAnUnrelatedCustomLifecycleIsConfigured_ThenEqualStructuralRefreshDoesNotInvokeIt()
    {
        // Expanding equal-container refresh to every ILifecycleInterceptor would add an undocumented callback
        // to custom implementations which do not own structural-property state.
        // Arrange
        var customLifecycle = new RecordingLifecycleInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleInterceptor>(customLifecycle);
        context.WithEqualityCheck();
        var parent = new MultipleLifecycleContainer(context);
        var items = parent.Items;
        items.Add(new Person { FirstName = "Child" });
        var attachedSubjectsBeforeRefresh = customLifecycle.AttachedSubjects.ToArray();

        // Act
        parent.Items = items;

        // Assert
        Assert.Equal(attachedSubjectsBeforeRefresh, customLifecycle.AttachedSubjects);
        Assert.Empty(customLifecycle.DetachedSubjects);
        Assert.Same(parent, Assert.Single(customLifecycle.AttachedSubjects));
    }

    [Fact]
    public void WhenTwoBuiltInLifecyclesRefreshTheSameContainer_ThenEachAuthorityRunsOnceAndSharedConsumersReplace()
    {
        // Selecting one structural capability, invoking one twice, or appending the two equivalent full
        // generations would break independent authority state or duplicate public relationship edges.
        // Arrange
        var firstLifecycle = new LifecycleInterceptor();
        var secondLifecycle = new LifecycleInterceptor();
        var customLifecycle = new RecordingLifecycleInterceptor();
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext.Create();
        context.AddService(firstLifecycle);
        context.AddService(secondLifecycle);
        context.AddService<ILifecycleInterceptor>(customLifecycle);
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);
        context
            .WithFullPropertyTracking()
            .WithParents()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();
        var parent = new MultipleLifecycleContainer(context);
        var first = new Person { FirstName = "First" };
        var second = new Person { FirstName = "Second" };
        var items = new List<Person> { first, first, second };
        relationshipHandler.Generations.Clear();
        parent.Items = items;

        Assert.Equal(2, relationshipHandler.Generations.Count);
        var secondAuthorityGeneration = relationshipHandler.Generations[0];
        var firstAuthorityGeneration = relationshipHandler.Generations[1];
        Assert.NotSame(secondAuthorityGeneration[0], firstAuthorityGeneration[0]);
        AssertSharedRelationships(parent, registry, first, second);
        Assert.Equal(2, first.GetReferenceCount());
        Assert.Equal(2, second.GetReferenceCount());
        var customAttachCountBeforeRefresh = customLifecycle.AttachedSubjects.Count;
        relationshipHandler.Generations.Clear();

        // Act
        parent.Items = items;

        // Assert
        Assert.Equal(2, relationshipHandler.Generations.Count);
        Assert.Same(firstAuthorityGeneration[0], relationshipHandler.Generations[0][0]);
        Assert.Same(secondAuthorityGeneration[0], relationshipHandler.Generations[1][0]);
        Assert.Equal(customAttachCountBeforeRefresh, customLifecycle.AttachedSubjects.Count);
        Assert.Empty(customLifecycle.DetachedSubjects);
        AssertSharedRelationships(parent, registry, first, second);
        Assert.Equal(2, first.GetReferenceCount());
        Assert.Equal(2, second.GetReferenceCount());
    }

    [Fact]
    public void WhenOneOfTwoAuthoritiesIsRemoved_ThenThePreservedLastCallbackOwnershipBoundaryClearsSharedViews()
    {
        // Preserved master boundary: shared consumers have no producer identity. Removing one overlapping
        // lifecycle authority therefore lets its last callback clear public ownership even while the other
        // authority retains independent canonical membership. This test pins the limitation; it is not a
        // guarantee that the still-attached authority keeps the shared registry view alive.
        // Arrange
        var registry = new SubjectRegistry();
        var parentTracking = new ParentTrackingHandler();
        var sharedConsumers = InterceptorSubjectContext.Create();
        sharedConsumers.AddService(registry);
        sharedConsumers.AddService(parentTracking);

        var firstLifecycle = new LifecycleInterceptor();
        var firstAuthority = InterceptorSubjectContext.Create();
        firstAuthority.AddService(firstLifecycle);
        var secondLifecycle = new LifecycleInterceptor();
        var secondAuthority = InterceptorSubjectContext.Create();
        secondAuthority.AddService(secondLifecycle);

        var parent = new MultipleLifecycleContainer();
        var subjectContext = ((IInterceptorSubject)parent).Context;
        subjectContext.AddFallbackContext(sharedConsumers);
        subjectContext.AddFallbackContext(firstAuthority);
        subjectContext.AddFallbackContext(secondAuthority);
        var child = new Person { FirstName = "Child" };
        parent.Items = [child];
        var registeredProperty = registry
            .TryGetRegisteredSubject(parent)!
            .TryGetProperty(nameof(MultipleLifecycleContainer.Items))!;
        Assert.Single(registeredProperty.Children);
        Assert.Single(child.GetParents());
        Assert.Equal(2, child.GetReferenceCount());

        // Act
        subjectContext.RemoveFallbackContext(firstAuthority);

        // Assert: this intentionally describes master's source-less last-callback behavior.
        Assert.Same(secondLifecycle, Assert.Single(subjectContext.GetServices<LifecycleInterceptor>()));
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Null(registry.TryGetRegisteredSubject(parent));
        Assert.Empty(registeredProperty.Children);
        Assert.Empty(child.GetParents());

        // Act: the remaining authority still owns independent canonical membership to remove.
        secondLifecycle.DetachSubjectFromContext(parent);

        // Assert
        Assert.Equal(0, child.GetReferenceCount());
    }

    private static void AssertSharedRelationships(
        MultipleLifecycleContainer parent,
        ISubjectRegistry registry,
        Person first,
        Person second)
    {
        var property = registry
            .TryGetRegisteredSubject(parent)!
            .TryGetProperty(nameof(MultipleLifecycleContainer.Items))!;
        Assert.Equal([first, first, second], property.Children.Select(child => child.Subject));
        Assert.Equal([0, 1, 2], property.Children.Select(child => child.Index));
        Assert.Equal([0, 1], registry.TryGetRegisteredSubject(first)!.Parents.Select(parent => parent.Index));
        Assert.Equal([2], registry.TryGetRegisteredSubject(second)!.Parents.Select(parent => parent.Index));
        Assert.Equal([0, 1], first.GetParents().Select(parent => parent.Index));
        Assert.Equal([2], second.GetParents().Select(parent => parent.Index));
    }

    private sealed class RecordingLifecycleInterceptor : ILifecycleInterceptor
    {
        public List<IInterceptorSubject> AttachedSubjects { get; } = [];

        public List<IInterceptorSubject> DetachedSubjects { get; } = [];

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            AttachedSubjects.Add(subject);
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            DetachedSubjects.Add(subject);
        }
    }

    private sealed class RecordingRelationshipHandler : IPropertyRelationshipHandler
    {
        public List<SubjectPropertyRelationship[]> Generations { get; } = [];

        public void ReconcileChildRelationships(
            PropertyReference property,
            ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            if (property.Name == nameof(MultipleLifecycleContainer.Items) && !relationships.IsEmpty)
            {
                Generations.Add(relationships.ToArray());
            }
        }
    }
}

[InterceptorSubject]
public partial class MultipleLifecycleContainer
{
    public MultipleLifecycleContainer()
    {
        Items = [];
    }

    public partial List<Person> Items { get; set; }
}

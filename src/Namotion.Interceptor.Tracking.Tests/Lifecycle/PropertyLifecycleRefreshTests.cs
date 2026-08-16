using System.Collections.Specialized;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class PropertyLifecycleRefreshTests
{
    [Fact]
    public void WhenAMutableListIsAssignedToItself_ThenItsRelationshipsAreReconciledWithoutAWrite()
    {
        // Removing structural refresh from the equality-suppressed path would leave the old order published.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithEqualityCheck()
            .WithPropertyChangeSubscriptions()
            .WithLifecycle();
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);

        var garage = new Garage(context);
        var first = new Car { Name = "first" };
        var second = new Car { Name = "second" };
        var cars = new List<Car> { first, second };
        garage.MutableCars = cars;

        var initialRelationships = Assert.Single(relationshipHandler.Generations);
        var property = new PropertyReference(garage, nameof(Garage.MutableCars));
        Assert.True(property.TryGetWriteState(true, out var initialRevision, out _));
        var notificationCount = 0;
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(_ => notificationCount++);
        relationshipHandler.Generations.Clear();
        cars.Reverse();

        // Act
        garage.MutableCars = cars;

        // Assert
        var relationships = Assert.Single(relationshipHandler.Generations);
        AssertRelationships(relationships, (second, 0), (first, 1));
        Assert.NotSame(initialRelationships[0], relationships[1]);
        Assert.NotSame(initialRelationships[1], relationships[0]);
        Assert.True(property.TryGetWriteState(true, out var finalRevision, out _));
        Assert.Equal(initialRevision, finalRevision);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public void WhenSeveralLifecycleInterceptorsRefreshAnEqualContainer_ThenEachRunsOnceInResolverOrder()
    {
        // Re-resolving capabilities during dispatch, invoking only one, or changing resolver order would duplicate
        // or swap the relationship generations owned by the two lifecycle authorities.
        // Arrange
        var firstLifecycle = new LifecycleInterceptor();
        var secondLifecycle = new LifecycleInterceptor();
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext.Create();
        context.AddService(firstLifecycle);
        context.AddService(secondLifecycle);
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);
        context.WithEqualityCheck();

        var garage = new Garage(context);
        var cars = new List<Car> { new() { Name = "first" } };
        garage.MutableCars = cars;

        Assert.Equal(2, relationshipHandler.Generations.Count);
        // Ordinary write interceptors reconcile while the chain unwinds, so the second authority publishes first.
        var secondAuthorityRelationship = Assert.Single(relationshipHandler.Generations[0]);
        var firstAuthorityRelationship = Assert.Single(relationshipHandler.Generations[1]);
        relationshipHandler.Generations.Clear();

        // Act
        garage.MutableCars = cars;

        // Assert
        Assert.Equal(2, relationshipHandler.Generations.Count);
        Assert.Same(firstAuthorityRelationship, Assert.Single(relationshipHandler.Generations[0]));
        Assert.Same(secondAuthorityRelationship, Assert.Single(relationshipHandler.Generations[1]));
    }

    [Fact]
    public void WhenAMutableDictionaryIsRekeyedAndAssignedToItself_ThenTheOpaqueKeyIsRefreshed()
    {
        // Comparing only the dictionary reference would preserve a relationship to the removed key object.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithEqualityCheck()
            .WithLifecycle();
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);

        var garage = new Garage(context);
        var child = new Car { Name = "child" };
        var firstKey = new object();
        var secondKey = new object();
        var dictionary = new OrderedDictionary { [firstKey] = child };
        garage.DictionaryItems = dictionary;

        var initialRelationship = Assert.Single(Assert.Single(relationshipHandler.Generations));
        relationshipHandler.Generations.Clear();
        dictionary.Remove(firstKey);
        dictionary.Add(secondKey, child);

        // Act
        garage.DictionaryItems = dictionary;

        // Assert
        var relationship = Assert.Single(Assert.Single(relationshipHandler.Generations));
        Assert.NotSame(initialRelationship, relationship);
        Assert.Same(secondKey, relationship.Index);
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenARelationshipHandlerWritesTheSameProperty_ThenReconciliationThrowsBeforeNestedProcessing()
    {
        // Nested reconciliation of the same baseline would let the inner generation be overwritten by the outer one.
        // Arrange
        var relationshipHandler = new ReentrantRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);

        var garage = new Garage(context);
        relationshipHandler.Callback = property =>
        {
            if (property.Name == nameof(Garage.MutableCars))
            {
                garage.MutableCars = [];
            }
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            garage.MutableCars = [new Car { Name = "child" }]);
    }

    [Fact]
    public void WhenARelationshipHandlerWritesAnotherProperty_ThenBothPropertiesAreReconciled()
    {
        // A global re-entry guard would reject the supported callback pattern where a different property is updated.
        // Arrange
        var relationshipHandler = new ReentrantRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);

        var garage = new Garage(context);
        var first = new Car { Name = "first" };
        var second = new Car { Name = "second" };
        relationshipHandler.Callback = property =>
        {
            if (property.Name == nameof(Garage.MutableCars))
            {
                garage.PrimaryCar = second;
            }
        };

        // Act
        garage.MutableCars = [first];

        // Assert
        Assert.Same(second, garage.PrimaryCar);
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
        Assert.Equal(
            [nameof(Garage.MutableCars), nameof(Garage.PrimaryCar)],
            relationshipHandler.Properties);
    }

    private static void AssertRelationships(
        SubjectPropertyRelationship[] relationships,
        params (IInterceptorSubject Child, object? Index)[] expected)
    {
        Assert.Equal(expected.Length, relationships.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Same(expected[index].Child, relationships[index].Child);
            Assert.Equal(expected[index].Index, relationships[index].Index);
        }
    }

    private sealed class RecordingRelationshipHandler : IPropertyRelationshipHandler
    {
        public List<SubjectPropertyRelationship[]> Generations { get; } = [];

        public void ReconcileChildRelationships(
            PropertyReference property,
            ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            Generations.Add(relationships.ToArray());
        }
    }

    private sealed class ReentrantRelationshipHandler : IPropertyRelationshipHandler
    {
        private bool _hasInvokedCallback;

        public Action<PropertyReference>? Callback { get; set; }

        public List<string> Properties { get; } = [];

        public void ReconcileChildRelationships(
            PropertyReference property,
            ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            Properties.Add(property.Name);
            if (!_hasInvokedCallback)
            {
                _hasInvokedCallback = true;
                Callback?.Invoke(property);
            }
        }
    }
}

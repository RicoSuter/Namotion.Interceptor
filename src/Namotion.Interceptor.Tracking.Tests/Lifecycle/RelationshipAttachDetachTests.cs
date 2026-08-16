using System.Collections;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class RelationshipAttachDetachTests
{
    [Fact]
    public void WhenInitialAttachStarts_ThenEveryStructuralPropertyIsEnumeratedBeforeTheFirstLifecycleCallback()
    {
        // Removing whole-parent staging would let the first child's attachment run before the second enumerable.
        // Arrange
        var events = new List<string>();
        var lifecycleHandler = new CallbackLifecycleHandler(change =>
            events.Add($"callback:{change.Subject}"));
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();
        context.AddService<ILifecycleHandler>(lifecycleHandler);

        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        var parent = new ThrowingStructuralContainer
        {
            FirstItems = new TrackingEnumerable<object?>("enumerated:first", events, first),
            SecondItems = new TrackingEnumerable<object?>("enumerated:second", events, second)
        };

        // Act
        ((IInterceptorSubject)parent).Context.AddFallbackContext(context);

        // Assert
        Assert.Equal("enumerated:first", events[0]);
        Assert.Equal("enumerated:second", events[1]);
        Assert.StartsWith("callback:", events[2]);
    }

    [Fact]
    public void WhenASecondPropertyFailsDuringInitialAttach_ThenNothingIsPublishedAndTheAttachCanBeRetried()
    {
        // Publishing the first property's state during staging would make the retry treat its unattached child as retained.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);

        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        var secondItems = new ThrowOnceEnumerable<object?>(second);
        var parent = new ThrowingStructuralContainer
        {
            FirstItems = [first],
            SecondItems = secondItems
        };
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            ((IInterceptorSubject)parent).Context.AddFallbackContext(context));
        Assert.Equal(0, first.GetReferenceCount());
        Assert.Equal(0, second.GetReferenceCount());
        Assert.Empty(relationshipHandler.Generations);

        // Act
        lifecycleInterceptor.AttachSubjectToContext(parent);

        // Assert
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
        Assert.Equal(
            [nameof(ThrowingStructuralContainer.FirstItems), nameof(ThrowingStructuralContainer.SecondItems)],
            relationshipHandler.Generations.Select(generation => generation.Property.Name));
        Assert.Same(first, Assert.Single(relationshipHandler.Generations[0].Relationships).Child);
        Assert.Same(second, Assert.Single(relationshipHandler.Generations[1].Relationships).Child);
    }

    [Fact]
    public void WhenBackingStorageChangesBeforeContextDetach_ThenCanonicalChildrenAndFirstOccurrenceMetadataAreDetached()
    {
        // Enumerating the mutated list would detach the replacement and lose the relationship that was actually attached.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);

        var attached = new Car { Name = "attached" };
        var replacement = new Car { Name = "replacement" };
        var items = new List<object?> { attached, attached };
        var parent = new ThrowingStructuralContainer
        {
            FirstItems = items
        };
        ((IInterceptorSubject)parent).Context.AddFallbackContext(context);
        var initialGeneration = Assert.Single(relationshipHandler.Generations);
        var firstRelationship = initialGeneration.Relationships[0];
        items[0] = replacement;
        items[1] = replacement;

        // Act
        ((IInterceptorSubject)parent).Context.RemoveFallbackContext(context);

        // Assert
        Assert.Equal(0, attached.GetReferenceCount());
        Assert.Equal(0, replacement.GetReferenceCount());
        var detachment = Assert.Single(attached.Detachements);
        Assert.Equal(0, detachment.Index);
        Assert.Same(firstRelationship, detachment.Relationship);
        var clearedGeneration = Assert.Single(
            relationshipHandler.Generations,
            generation => generation.Property.Name == nameof(ThrowingStructuralContainer.FirstItems) &&
                          generation.Relationships.Length == 0);
        Assert.Empty(clearedGeneration.Relationships);
    }

    [Fact]
    public void WhenAChildAttachReentrantlyDetachesTheParent_ThenTheInitialAttachIsAbortedWithoutLeaks()
    {
        // Treating attach-in-progress as graph membership would let the cancelled generation finish and leak later additions.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        var parent = new ThrowingStructuralContainer
        {
            FirstItems = [first],
            SecondItems = [second]
        };
        var detachHandler = new DetachParentLifecycleHandler(lifecycleInterceptor, parent, first);
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);
        context.AddService<ILifecycleHandler>(detachHandler);
        var parentAttachCount = 0;
        lifecycleInterceptor.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, parent))
            {
                parentAttachCount++;
            }
        };

        // Act
        ((IInterceptorSubject)parent).Context.AddFallbackContext(context);

        // Assert
        Assert.Equal(0, parentAttachCount);
        Assert.Equal(0, first.GetReferenceCount());
        Assert.Equal(0, second.GetReferenceCount());
        Assert.Equal(
            [nameof(ThrowingStructuralContainer.FirstItems), nameof(ThrowingStructuralContainer.SecondItems)],
            relationshipHandler.Generations.Select(generation => generation.Property.Name));
        Assert.All(relationshipHandler.Generations, generation => Assert.Empty(generation.Relationships));

        // Arrange
        relationshipHandler.Generations.Clear();
        detachHandler.Enabled = false;
        var replacement = new Person { FirstName = "replacement" };
        parent.FirstItems = [replacement];
        parent.SecondItems = [];

        // Act
        lifecycleInterceptor.AttachSubjectToContext(parent);

        // Assert
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Equal(0, first.GetReferenceCount());
        Assert.Equal(0, second.GetReferenceCount());
        var published = Assert.Single(
            relationshipHandler.Generations,
            generation => generation.Relationships.Length > 0);
        Assert.Same(replacement, Assert.Single(published.Relationships).Child);
    }

    [Fact]
    public void WhenALifecycleHandlerThrowsDuringInitialAttach_ThenALaterAttachIsNotBlocked()
    {
        // Forgetting the attach token in the exceptional path would make the retry return without reaching root attach.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var child = new Person { FirstName = "child" };
        var parent = new ThrowingStructuralContainer
        {
            FirstItems = [child]
        };
        var throwingHandler = new ThrowOnceLifecycleHandler(child);
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);
        context.AddService<ILifecycleHandler>(throwingHandler);
        var parentAttachCount = 0;
        lifecycleInterceptor.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, parent))
            {
                parentAttachCount++;
            }
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            ((IInterceptorSubject)parent).Context.AddFallbackContext(context));

        // Act
        lifecycleInterceptor.AttachSubjectToContext(parent);

        // Assert
        Assert.Equal(1, parentAttachCount);
        Assert.Equal(1, child.GetReferenceCount());
        var published = Assert.Single(
            relationshipHandler.Generations,
            generation => generation.Relationships.Length > 0);
        Assert.Same(child, Assert.Single(published.Relationships).Child);
    }

    private sealed class RecordingRelationshipHandler : IPropertyRelationshipHandler
    {
        public List<RelationshipGeneration> Generations { get; } = [];

        public void ReconcileChildRelationships(
            PropertyReference property,
            ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            Generations.Add(new RelationshipGeneration(property, relationships.ToArray()));
        }
    }

    private sealed record RelationshipGeneration(
        PropertyReference Property,
        SubjectPropertyRelationship[] Relationships);

    private sealed class CallbackLifecycleHandler(Action<SubjectLifecycleChange> callback) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change) => callback(change);
    }

    private sealed class DetachParentLifecycleHandler(
        LifecycleInterceptor lifecycleInterceptor,
        IInterceptorSubject parent,
        IInterceptorSubject trigger) : ILifecycleHandler
    {
        public bool Enabled { get; set; } = true;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (Enabled &&
                ReferenceEquals(change.Subject, trigger) &&
                change.IsPropertyReferenceAdded)
            {
                lifecycleInterceptor.DetachSubjectFromContext(parent);
            }
        }
    }

    private sealed class ThrowOnceLifecycleHandler(IInterceptorSubject trigger) : ILifecycleHandler
    {
        private bool _hasThrown;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!_hasThrown &&
                ReferenceEquals(change.Subject, trigger) &&
                change.IsPropertyReferenceAdded)
            {
                _hasThrown = true;
                throw new InvalidOperationException("Lifecycle callback failed.");
            }
        }
    }

    private sealed class TrackingEnumerable<T>(
        string eventName,
        List<string> events,
        params T[] items) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator()
        {
            events.Add(eventName);
            return ((IEnumerable<T>)items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowOnceEnumerable<T>(params T[] items) : IEnumerable<T>
    {
        private bool _hasThrown;

        public IEnumerator<T> GetEnumerator()
        {
            if (!_hasThrown)
            {
                _hasThrown = true;
                throw new InvalidOperationException("Enumeration failed.");
            }

            return ((IEnumerable<T>)items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

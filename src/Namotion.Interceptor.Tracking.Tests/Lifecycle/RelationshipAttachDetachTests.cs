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
        var laterLifecycleHandler = new RecordingLifecycleHandler(first);
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);
        context.AddService<ILifecycleHandler>(detachHandler);
        context.AddService<ILifecycleHandler>(laterLifecycleHandler);
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
        Assert.Equal(0, laterLifecycleHandler.AdditionCount);
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
    public void WhenTheFirstPropertyLifecycleHandlerCancelsInitialAttach_ThenLaterAttachHandlersDoNotRun()
    {
        // Checking cancellation only after a complete property-handler batch would invoke the later handler.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var child = new Person { FirstName = "child" };
        var parent = new ThrowingStructuralContainer
        {
            FirstItems = [child]
        };
        var cancellingHandler = new DetachParentPropertyLifecycleHandler(
            lifecycleInterceptor,
            parent,
            child);
        var laterHandler = new RecordingPropertyLifecycleHandler(child);
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);
        context.AddService<IPropertyLifecycleHandler>(cancellingHandler);
        context.AddService<IPropertyLifecycleHandler>(laterHandler);

        // Act
        ((IInterceptorSubject)parent).Context.AddFallbackContext(context);

        // Assert
        Assert.Equal(1, cancellingHandler.AttachCount);
        Assert.Equal(0, laterHandler.AttachCount);
        Assert.Equal(0, child.GetReferenceCount());
        Assert.All(relationshipHandler.Generations, generation => Assert.Empty(generation.Relationships));
    }

    [Fact]
    public void WhenTheFirstRelationshipHandlerCancelsInitialAttach_ThenNoLaterNonEmptyGroupIsDispatched()
    {
        // A full relationship-handler batch or later-property loop would republish the cancelled generation.
        // Arrange
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
        var cancellingHandler = new DetachParentRelationshipHandler(lifecycleInterceptor, parent);
        var laterHandler = new RecordingRelationshipHandler();
        context.AddService<IPropertyRelationshipHandler>(cancellingHandler);
        context.AddService<IPropertyRelationshipHandler>(laterHandler);

        // Act
        ((IInterceptorSubject)parent).Context.AddFallbackContext(context);

        // Assert
        Assert.Single(cancellingHandler.Generations, generation => generation.Relationships.Length > 0);
        Assert.DoesNotContain(laterHandler.Generations, generation => generation.Relationships.Length > 0);
        Assert.Equal(0, first.GetReferenceCount());
        Assert.Equal(0, second.GetReferenceCount());
        Assert.Empty(laterHandler.GetLastRelationships(
            new PropertyReference(parent, nameof(ThrowingStructuralContainer.FirstItems))));
        Assert.Empty(laterHandler.GetLastRelationships(
            new PropertyReference(parent, nameof(ThrowingStructuralContainer.SecondItems))));
    }

    [Fact]
    public void WhenASelfReferenceCancelsBeforeRootAttach_ThenItsProvisionalMembershipIsUndone()
    {
        // Treating the self-membership as a root attach would remove it without decrementing its reference count.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var parent = new ThrowingStructuralContainer();
        parent.FirstItems = [parent];
        var detachHandler = new DetachParentLifecycleHandler(lifecycleInterceptor, parent, parent);
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);
        context.AddService<ILifecycleHandler>(detachHandler);

        // Act
        ((IInterceptorSubject)parent).Context.AddFallbackContext(context);

        // Assert
        Assert.Equal(0, parent.GetReferenceCount());
        Assert.All(relationshipHandler.Generations, generation => Assert.Empty(generation.Relationships));

        // Arrange
        relationshipHandler.Generations.Clear();
        detachHandler.Enabled = false;
        parent.FirstItems = [];

        // Act
        lifecycleInterceptor.AttachSubjectToContext(parent);
        lifecycleInterceptor.DetachSubjectFromContext(parent);

        // Assert
        Assert.Equal(0, parent.GetReferenceCount());
        Assert.All(relationshipHandler.Generations, generation => Assert.Empty(generation.Relationships));
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

    [Fact]
    public void WhenALifecycleCallbackWritesAnotherProperty_ThenBothCanonicalGenerationsConverge()
    {
        // A subject-wide re-entry guard would reject the supported callback write even though the second
        // property has an independent canonical baseline.
        // Arrange
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();
        var parent = new ThrowingStructuralContainer();
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        var lifecycleHandler = new CallbackLifecycleHandler(change =>
        {
            if (ReferenceEquals(change.Subject, first) &&
                change.IsPropertyReferenceAdded &&
                change.Property is { } property &&
                property.Name == nameof(ThrowingStructuralContainer.FirstItems))
            {
                parent.SecondItems = [second];
            }
        });
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);
        context.AddService<ILifecycleHandler>(lifecycleHandler);
        ((IInterceptorSubject)parent).Context.AddFallbackContext(context);
        relationshipHandler.Generations.Clear();

        // Act
        parent.FirstItems = [first];

        // Assert
        Assert.Same(first, Assert.Single(relationshipHandler.GetLastRelationships(
            new PropertyReference(parent, nameof(ThrowingStructuralContainer.FirstItems)))).Child);
        Assert.Same(second, Assert.Single(relationshipHandler.GetLastRelationships(
            new PropertyReference(parent, nameof(ThrowingStructuralContainer.SecondItems)))).Child);
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
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

        public SubjectPropertyRelationship[] GetLastRelationships(PropertyReference property)
        {
            return Generations
                .Last(generation => PropertyReference.Comparer.Equals(generation.Property, property))
                .Relationships;
        }
    }

    private sealed class DetachParentRelationshipHandler(
        LifecycleInterceptor lifecycleInterceptor,
        IInterceptorSubject parent) : IPropertyRelationshipHandler
    {
        private bool _hasCancelled;

        public List<RelationshipGeneration> Generations { get; } = [];

        public void ReconcileChildRelationships(
            PropertyReference property,
            ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            Generations.Add(new RelationshipGeneration(property, relationships.ToArray()));
            if (!_hasCancelled && !relationships.IsEmpty)
            {
                _hasCancelled = true;
                lifecycleInterceptor.DetachSubjectFromContext(parent);
            }
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

    private sealed class RecordingLifecycleHandler(IInterceptorSubject trigger) : ILifecycleHandler
    {
        public int AdditionCount { get; private set; }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (ReferenceEquals(change.Subject, trigger) &&
                change.IsPropertyReferenceAdded)
            {
                AdditionCount++;
            }
        }
    }

    private sealed class DetachParentPropertyLifecycleHandler(
        LifecycleInterceptor lifecycleInterceptor,
        IInterceptorSubject parent,
        IInterceptorSubject trigger) : IPropertyLifecycleHandler
    {
        private bool _hasCancelled;

        public int AttachCount { get; private set; }

        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
            if (!_hasCancelled && ReferenceEquals(change.Subject, trigger))
            {
                _hasCancelled = true;
                AttachCount++;
                lifecycleInterceptor.DetachSubjectFromContext(parent);
            }
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
        }
    }

    private sealed class RecordingPropertyLifecycleHandler(IInterceptorSubject trigger) : IPropertyLifecycleHandler
    {
        public int AttachCount { get; private set; }

        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
            if (ReferenceEquals(change.Subject, trigger))
            {
                AttachCount++;
            }
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
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

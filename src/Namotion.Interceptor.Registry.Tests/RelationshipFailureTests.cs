using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

public class RelationshipFailureTests
{
    [Fact]
    public void WhenRegistryAppearsAfterRelationshipHandlerCapture_ThenProvisionalRelationshipIsLaterReplacedByFullGeneration()
    {
        // Capturing no relationship handlers before next() must remain compatible with Registry appearing
        // before the later lifecycle-handler resolution. The first operation may seed only its lifecycle
        // occurrence; a later operation captures Registry and must replace that provisional group exactly.
        // Arrange
        var lifecycle = new LifecycleInterceptor();
        var registry = new SubjectRegistry();
        var addRegistryInterceptor = new AddRegistryDuringWriteInterceptor(registry);
        var lifecycleHandler = new RecordingLifecycleHandler();
        var context = InterceptorSubjectContext.Create();
        context.AddService(lifecycle);
        context.AddService(addRegistryInterceptor);
        context.AddService<ILifecycleHandler>(lifecycleHandler);
        var parent = new TopologyRelationshipContainer(context);
        var child = new Person { FirstName = "Child" };
        var firstKey = new EqualityTripwireKey("first");
        var secondKey = new EqualityTripwireKey("second");
        var writtenValue = new Dictionary<EqualityTripwireKey, Person>
        {
            [firstKey] = child,
            [secondKey] = child
        };
        firstKey.Arm();
        secondKey.Arm();
        lifecycleHandler.Changes.Clear();
        addRegistryInterceptor.IsArmed = true;

        // Act
        var exception = Record.Exception(() => parent.Items = writtenValue);

        // Assert
        Assert.Null(exception);
        Assert.Same(writtenValue, parent.Items);
        var addition = Assert.Single(
            lifecycleHandler.Changes,
            change => ReferenceEquals(change.Subject, child) && change.IsPropertyReferenceAdded);
        Assert.Null(addition.Relationship);
        Assert.Same(firstKey, addition.Index);
        Assert.Equal(nameof(TopologyRelationshipContainer.Items), addition.Property?.Name);

        var registeredProperty = registry
            .TryGetRegisteredSubject(parent)!
            .TryGetProperty(nameof(TopologyRelationshipContainer.Items))!;
        var provisionalChild = Assert.Single(registeredProperty.Children);
        var provisionalParent = Assert.Single(registry.TryGetRegisteredSubject(child)!.Parents);
        Assert.Same(child, provisionalChild.Subject);
        Assert.Same(firstKey, provisionalChild.Index);
        Assert.Same(parent, provisionalParent.Property.Subject);
        Assert.Same(firstKey, provisionalParent.Index);
        Assert.Equal(1, child.GetReferenceCount());
        Assert.False(firstKey.WasInvoked);
        Assert.False(secondKey.WasInvoked);
        lifecycleHandler.Changes.Clear();

        // Act: Registry is now present in the captured relationship-handler snapshot.
        parent.Items = writtenValue;

        // Assert
        Assert.Empty(lifecycleHandler.Changes);
        var children = registeredProperty.Children;
        var parents = registry.TryGetRegisteredSubject(child)!.Parents;
        Assert.Equal(2, children.Length);
        Assert.Equal(2, parents.Length);
        Assert.All(children, relationship => Assert.Same(child, relationship.Subject));
        Assert.All(parents, relationship => Assert.Same(parent, relationship.Property.Subject));
        Assert.Same(firstKey, children[0].Index);
        Assert.Same(secondKey, children[1].Index);
        Assert.Same(firstKey, parents[0].Index);
        Assert.Same(secondKey, parents[1].Index);
        Assert.Equal(1, child.GetReferenceCount());
        Assert.False(firstKey.WasInvoked);
        Assert.False(secondKey.WasInvoked);
    }

    [Fact]
    public void WhenTheFirstRelationshipConsumerThrows_ThenLaterConsumersConvergeBeforeTheOriginalExceptionIsRethrown()
    {
        // Stopping at the first custom consumer would strand the registry, parent tracker, and subject
        // handler on the previous generation even though lifecycle canonical state already committed.
        // Arrange
        var calls = new List<string>();
        var throwingHandler = new ThrowingRelationshipHandler(calls);
        var recordingHandler = new RecordingRelationshipHandler(calls);
        var context = InterceptorSubjectContext.Create();
        context.AddService<IPropertyRelationshipHandler>(throwingHandler);
        context.AddService<IPropertyRelationshipHandler>(recordingHandler);
        context
            .WithFullPropertyTracking()
            .WithParents()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();
        var parent = new RelationshipFailureContainer(context)
        {
            RelationshipHandlerCallOrder = calls,
            ThrowAfterRecording = true
        };
        var child = new Person { FirstName = "Child" };
        var writtenValue = new[] { child, child };
        throwingHandler.IsArmed = true;
        recordingHandler.Generations.Clear();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            parent.Children = writtenValue);

        // Assert
        Assert.Same(throwingHandler.ExpectedException, exception);
        Assert.Contains(nameof(ThrowExpectedException), exception.StackTrace);
        Assert.Equal(["throwing", "recording", "subject"], calls);
        Assert.Same(writtenValue, parent.Children);

        var recordedRelationships = Assert.Single(recordingHandler.Generations);
        var subjectRelationships = Assert.Single(parent.RelationshipReconciliations);
        Assert.Equal(2, recordedRelationships.Length);
        Assert.Equal(2, subjectRelationships.Length);
        for (var index = 0; index < recordedRelationships.Length; index++)
        {
            Assert.Same(recordedRelationships[index], subjectRelationships[index]);
            Assert.Same(child, recordedRelationships[index].Child);
            Assert.Equal(index, recordedRelationships[index].Index);
        }

        var registeredProperty = registry
            .TryGetRegisteredSubject(parent)!
            .TryGetProperty(nameof(RelationshipFailureContainer.Children))!;
        var registryChildren = registeredProperty.Children;
        var registryParents = registry.TryGetRegisteredSubject(child)!.Parents;
        var trackedParents = child.GetParents();
        Assert.Equal([0, 1], registryChildren.Select(relationship => relationship.Index));
        Assert.Equal([0, 1], registryParents.Select(relationship => relationship.Index));
        Assert.Equal([0, 1], trackedParents.Select(relationship => relationship.Index));
        Assert.All(registryChildren, relationship => Assert.Same(child, relationship.Subject));
        Assert.All(registryParents, relationship => Assert.Same(parent, relationship.Property.Subject));
        Assert.All(trackedParents, relationship => Assert.Same(parent, relationship.Property.Subject));
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenLifecycleAdditionFailsAfterRegistryPublication_ThenRetryReplacesTheProvisionalSnapshot()
    {
        // Leaving provisional tombstones after a partial lifecycle attempt would duplicate or reorder the
        // successful retry, while mutating the failed snapshot would hide the topology seen at the failure.
        // Arrange
        var throwingHandler = new ThrowOnceAfterRegistryLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        context.AddService<ILifecycleHandler>(throwingHandler);
        var parent = new RelationshipFailureContainer(context);
        var first = new Person { FirstName = "First" };
        var second = new Person { FirstName = "Second" };
        var writtenValue = new[] { first, second };
        var property = parent.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(RelationshipFailureContainer.Children))!;
        throwingHandler.Arm(parent);

        // Act & Assert: Registry has applied the first provisional addition when the later handler fails.
        var exception = Assert.Throws<InvalidOperationException>(() => parent.Children = writtenValue);
        Assert.Same(throwingHandler.ExpectedException, exception);
        Assert.Same(writtenValue, parent.Children);
        var failedSnapshot = property.Children;
        var provisionalChild = Assert.Single(failedSnapshot);
        Assert.Same(first, provisionalChild.Subject);
        Assert.Equal(0, provisionalChild.Index);

        // Act: retry the already-written generation after the one-shot failure.
        parent.Children = writtenValue;

        // Assert
        var finalSnapshot = property.Children;
        Assert.Equal(2, finalSnapshot.Length);
        Assert.Same(first, finalSnapshot[0].Subject);
        Assert.Equal(0, finalSnapshot[0].Index);
        Assert.Same(second, finalSnapshot[1].Subject);
        Assert.Equal(1, finalSnapshot[1].Index);
        provisionalChild = Assert.Single(failedSnapshot);
        Assert.Same(first, provisionalChild.Subject);
        Assert.Equal(0, provisionalChild.Index);
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowExpectedException(Exception exception)
    {
        throw exception;
    }

    private sealed class ThrowingRelationshipHandler(List<string> calls) : IPropertyRelationshipHandler
    {
        public InvalidOperationException ExpectedException { get; } = new("First relationship consumer failed.");

        public bool IsArmed { get; set; }

        public void ReconcileChildRelationships(
            PropertyReference property,
            ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            if (!IsArmed || relationships.IsEmpty)
            {
                return;
            }

            calls.Add("throwing");
            ThrowExpectedException(ExpectedException);
        }
    }

    private sealed class RecordingRelationshipHandler(List<string> calls) : IPropertyRelationshipHandler
    {
        public List<SubjectPropertyRelationship[]> Generations { get; } = [];

        public void ReconcileChildRelationships(
            PropertyReference property,
            ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            if (relationships.IsEmpty)
            {
                return;
            }

            calls.Add("recording");
            Generations.Add(relationships.ToArray());
        }
    }

    private sealed class RecordingLifecycleHandler : ILifecycleHandler
    {
        public List<SubjectLifecycleChange> Changes { get; } = [];

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            Changes.Add(change);
        }
    }

    [RunsAfter(typeof(SubjectRegistry))]
    private sealed class ThrowOnceAfterRegistryLifecycleHandler : ILifecycleHandler
    {
        private IInterceptorSubject? _parent;

        public InvalidOperationException ExpectedException { get; } = new("Lifecycle addition failed.");

        public void Arm(IInterceptorSubject parent)
        {
            _parent = parent;
        }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (_parent is not null &&
                change.IsPropertyReferenceAdded &&
                ReferenceEquals(change.Property?.Subject, _parent))
            {
                _parent = null;
                throw ExpectedException;
            }
        }
    }

    public sealed class EqualityTripwireKey(string value)
    {
        private bool _isArmed;

        public bool WasInvoked { get; private set; }

        public void Arm()
        {
            WasInvoked = false;
            _isArmed = true;
        }

        public override bool Equals(object? obj)
        {
            WasInvoked = true;
            if (_isArmed)
            {
                throw new InvalidOperationException("Dictionary-key equality must not run during reconciliation.");
            }

            return ReferenceEquals(this, obj);
        }

        public override int GetHashCode()
        {
            WasInvoked = true;
            if (_isArmed)
            {
                throw new InvalidOperationException("Dictionary-key hashing must not run during reconciliation.");
            }

            return RuntimeHelpers.GetHashCode(this);
        }

        public override string ToString() => value;
    }

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class AddRegistryDuringWriteInterceptor(SubjectRegistry registry) : IWriteInterceptor
    {
        public bool IsArmed { get; set; }

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (IsArmed)
            {
                IsArmed = false;
                context.Property.Subject.Context.AddService(registry);
            }

            next(ref context);
        }
    }
}

[InterceptorSubject]
public partial class RelationshipFailureContainer : IPropertyRelationshipHandler
{
    public RelationshipFailureContainer()
    {
        Children = [];
    }

    public partial Person[] Children { get; set; }

    public List<string>? RelationshipHandlerCallOrder { get; set; }

    public bool ThrowAfterRecording { get; set; }

    public List<SubjectPropertyRelationship[]> RelationshipReconciliations { get; } = [];

    public void ReconcileChildRelationships(
        PropertyReference property,
        ReadOnlySpan<SubjectPropertyRelationship> relationships)
    {
        if (relationships.IsEmpty)
        {
            return;
        }

        RelationshipHandlerCallOrder?.Add("subject");
        RelationshipReconciliations.Add(relationships.ToArray());
        if (ThrowAfterRecording)
        {
            throw new ApplicationException("Subject relationship consumer failed.");
        }
    }
}

[InterceptorSubject]
public partial class TopologyRelationshipContainer
{
    public TopologyRelationshipContainer()
    {
        Items = new Dictionary<RelationshipFailureTests.EqualityTripwireKey, Person>();
    }

    public partial Dictionary<RelationshipFailureTests.EqualityTripwireKey, Person> Items { get; set; }
}

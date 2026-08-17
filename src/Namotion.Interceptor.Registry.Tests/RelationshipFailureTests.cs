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
    public void WhenRegistryAppearsAfterRelationshipHandlerCapture_ThenLifecycleMetadataSeedsItsProvisionalRelationship()
    {
        // Capturing no relationship handlers before next() must remain compatible with Registry appearing
        // before the later lifecycle-handler resolution. Otherwise the membership commits with a null
        // Relationship and Registry throws instead of using the always-present property, subject, and index.
        // Arrange
        var lifecycle = new LifecycleInterceptor();
        var registry = new SubjectRegistry();
        var addRegistryInterceptor = new AddRegistryDuringWriteInterceptor(registry);
        var context = InterceptorSubjectContext.Create();
        context.AddService(lifecycle);
        context.AddService(addRegistryInterceptor);
        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };
        var writtenValue = new[] { child };
        addRegistryInterceptor.IsArmed = true;

        // Act
        var exception = Record.Exception(() => parent.Children = writtenValue);

        // Assert
        Assert.Null(exception);
        Assert.Same(writtenValue, parent.Children);
        var relationship = Assert.Single(registry
            .TryGetRegisteredSubject(parent)!
            .TryGetProperty(nameof(Person.Children))!
            .Children);
        var parentRelationship = Assert.Single(registry.TryGetRegisteredSubject(child)!.Parents);
        Assert.Same(child, relationship.Subject);
        Assert.Equal(0, relationship.Index);
        Assert.Same(parent, parentRelationship.Property.Subject);
        Assert.Equal(0, parentRelationship.Index);
        Assert.Equal(1, child.GetReferenceCount());
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
        var writtenValue = new[] { child };
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

        var recordedRelationship = Assert.Single(Assert.Single(recordingHandler.Generations));
        var subjectRelationship = Assert.Single(Assert.Single(parent.RelationshipReconciliations));
        Assert.Same(recordedRelationship, subjectRelationship);
        Assert.Same(child, recordedRelationship.Child);
        Assert.Equal(0, recordedRelationship.Index);

        var registeredProperty = registry
            .TryGetRegisteredSubject(parent)!
            .TryGetProperty(nameof(RelationshipFailureContainer.Children))!;
        var registryChild = Assert.Single(registeredProperty.Children);
        var registryParent = Assert.Single(registry.TryGetRegisteredSubject(child)!.Parents);
        var trackedParent = Assert.Single(child.GetParents());
        Assert.Same(child, registryChild.Subject);
        Assert.Equal(0, registryChild.Index);
        Assert.Same(parent, registryParent.Property.Subject);
        Assert.Equal(0, registryParent.Index);
        Assert.Same(parent, trackedParent.Property.Subject);
        Assert.Equal(0, trackedParent.Index);
        Assert.Equal(1, child.GetReferenceCount());
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

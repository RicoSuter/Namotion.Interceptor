using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class PropertyRelationshipHandlerTests
{
    [Fact]
    public void WhenContextAndSubjectRelationshipHandlersArePresent_ThenTheyReceiveEveryGenerationInResolverOrder()
    {
        // A dispatcher which skips an empty generation, reorders resolver services, or invokes the subject before
        // context services would leave consumers with divergent views of the same parent property.
        // Arrange
        var calls = new List<string>();
        var firstHandler = new RecordingRelationshipHandler("first", calls);
        var secondHandler = new RecordingRelationshipHandler("second", calls);
        var context = InterceptorSubjectContext.Create();
        context.AddService<IPropertyRelationshipHandler>(firstHandler);
        context.AddService<IPropertyRelationshipHandler>(secondHandler);

        var parent = new SelfHandlingContainer(context)
        {
            RelationshipHandlerCallOrder = calls
        };
        var property = new PropertyReference(parent, nameof(SelfHandlingContainer.Items));
        var firstChild = new Person();
        var secondChild = new Person();
        var relationships = new[]
        {
            new SubjectPropertyRelationship(property, firstChild, "first"),
            new SubjectPropertyRelationship(property, secondChild, "second")
        };
        var handlers = context.GetServices<IPropertyRelationshipHandler>();

        // Act
        parent.ReconcileChildRelationships(handlers, property, relationships);
        parent.ReconcileChildRelationships(handlers, property, []);

        // Assert
        Assert.Equal(
            ["first", "second", "subject", "first", "second", "subject"],
            calls);
        Assert.Equal([firstChild, secondChild], firstHandler.Generations[0].Select(relationship => relationship.Child));
        Assert.Empty(firstHandler.Generations[1]);
        Assert.Equal([firstChild, secondChild], parent.RelationshipReconciliations[0].Select(relationship => relationship.Child));
        Assert.Empty(parent.RelationshipReconciliations[1]);
    }

    [Fact]
    public void WhenAHandlerRetainsARelationship_ThenALaterGenerationDoesNotChangeIt()
    {
        // A mutable relationship object would let the later generation corrupt a consumer's retained view.
        // Arrange
        var retainingHandler = new RetainingRelationshipHandler();
        var context = InterceptorSubjectContext.Create();
        context.AddService<IPropertyRelationshipHandler>(retainingHandler);

        var parent = new SelfHandlingContainer(context);
        var firstProperty = new PropertyReference(parent, nameof(SelfHandlingContainer.Items));
        var firstChild = new Person();
        var firstIndex = new object();
        var firstRelationship = new SubjectPropertyRelationship(firstProperty, firstChild, firstIndex);
        var handlers = context.GetServices<IPropertyRelationshipHandler>();

        // Act
        parent.ReconcileChildRelationships(handlers, firstProperty, [firstRelationship]);

        var laterProperty = new PropertyReference(parent, nameof(SelfHandlingContainer.Items));
        parent.ReconcileChildRelationships(
            handlers,
            laterProperty,
            [new SubjectPropertyRelationship(laterProperty, new Person(), new object())]);

        // Assert
        var retainedRelationship = Assert.Single(retainingHandler.RetainedRelationships);
        Assert.Same(firstRelationship, retainedRelationship);
        Assert.Same(parent, retainedRelationship.Parent.Subject);
        Assert.Equal(nameof(SelfHandlingContainer.Items), retainedRelationship.Parent.Name);
        Assert.Same(firstChild, retainedRelationship.Child);
        Assert.Same(firstIndex, retainedRelationship.Index);
    }

    [Fact]
    public void WhenARelationshipHandlerThrows_ThenLaterHandlersRunAndTheFirstExceptionIsRethrown()
    {
        // A throwing custom handler must not prevent later built-in consumers from reconciling.
        // Arrange
        var calls = new List<string>();
        var expectedException = new InvalidOperationException("first");
        var throwingHandler = new ThrowingRelationshipHandler("first", calls, expectedException);
        var secondHandler = new ThrowingRelationshipHandler("second", calls, new ApplicationException("second"));
        var context = InterceptorSubjectContext.Create();
        context.AddService<IPropertyRelationshipHandler>(throwingHandler);
        context.AddService<IPropertyRelationshipHandler>(secondHandler);

        var parent = new SelfHandlingContainer(context)
        {
            RelationshipHandlerCallOrder = calls
        };
        var property = new PropertyReference(parent, nameof(SelfHandlingContainer.Items));

        // Act
        var actualException = Assert.Throws<InvalidOperationException>(() =>
            parent.ReconcileChildRelationships(context.GetServices<IPropertyRelationshipHandler>(), property, []));

        // Assert
        Assert.Same(expectedException, actualException);
        Assert.Equal(["first", "second", "subject"], calls);
    }

    private sealed class RecordingRelationshipHandler(string name, List<string> calls) : IPropertyRelationshipHandler
    {
        public List<SubjectPropertyRelationship[]> Generations { get; } = [];

        public void ReconcileChildRelationships(PropertyReference property, ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            calls.Add(name);
            Generations.Add(relationships.ToArray());
        }
    }

    private sealed class RetainingRelationshipHandler : IPropertyRelationshipHandler
    {
        public List<SubjectPropertyRelationship> RetainedRelationships { get; } = [];

        public void ReconcileChildRelationships(PropertyReference property, ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            if (RetainedRelationships.Count == 0 && !relationships.IsEmpty)
            {
                RetainedRelationships.Add(relationships[0]);
            }
        }
    }

    private sealed class ThrowingRelationshipHandler(string name, List<string> calls, Exception exception) : IPropertyRelationshipHandler
    {
        public void ReconcileChildRelationships(PropertyReference property, ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            calls.Add(name);
            throw exception;
        }
    }
}

using System.Collections.Immutable;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

public class ProvisionalRelationshipAllocationTests
{
    [Fact]
    public void WhenProvisionalRelationshipCountScalesByEight_ThenAllocationsRemainLinear()
    {
        // Rebuilding the complete immutable relationship array for every provisional change would make
        // the 1,024-member replacement allocate quadratically relative to the 128-member replacement.
        // Arrange
        _ = MeasureProvisionalMutationAllocations(128); // JIT warm-up

        // Act
        var smallAllocation = MeasureProvisionalMutationAllocations(128);
        var largeAllocation = MeasureProvisionalMutationAllocations(1024);

        // Assert
        Assert.True(largeAllocation < smallAllocation * 20,
            $"Expected linear allocation scaling, got {smallAllocation} and {largeAllocation} bytes.");
    }

    private static long MeasureProvisionalMutationAllocations(int count)
    {
        var relationshipHandler = new RecordingRelationshipHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        context.AddService<IPropertyRelationshipHandler>(relationshipHandler);

        var parent = new RelationshipShapeContainer(context);
        var oldSubjects = new Person[count];
        var newSubjects = new Person[count];
        for (var index = 0; index < count; index++)
        {
            oldSubjects[index] = new Person();
            newSubjects[index] = new Person();
        }

        parent.Array = oldSubjects;
        var oldRelationships = relationshipHandler.Generations[^1];
        parent.Array = newSubjects;
        var newRelationships = relationshipHandler.Generations[^1];
        var property = parent.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(RelationshipShapeContainer.Array))!;
        property.ReplaceChildRelationships(oldRelationships.ToImmutableArray());

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            property.RemoveChildRelationships(oldSubjects[index]);
        }

        for (var index = 0; index < count; index++)
        {
            property.AddChildRelationship(newRelationships[index]);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(property);
        GC.KeepAlive(oldSubjects);
        GC.KeepAlive(newSubjects);
        GC.KeepAlive(oldRelationships);
        GC.KeepAlive(newRelationships);
        return allocated;
    }

    private sealed class RecordingRelationshipHandler : IPropertyRelationshipHandler
    {
        public List<SubjectPropertyRelationship[]> Generations { get; } = [];

        public void ReconcileChildRelationships(
            PropertyReference property,
            ReadOnlySpan<SubjectPropertyRelationship> relationships)
        {
            if (property.Subject is RelationshipShapeContainer &&
                property.Name == nameof(RelationshipShapeContainer.Array))
            {
                Generations.Add(relationships.ToArray());
            }
        }
    }
}

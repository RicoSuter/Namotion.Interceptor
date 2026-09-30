using System.Reactive.Concurrency;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Verifies how <see cref="SubjectSourceExtensions.WriteChangesInBatchesAsync"/> slices changes for a source
/// that writes one change at a time, by applying the update built from each slice to a mirror, as a connector
/// that sends one update per write does.
/// </summary>
public class SubjectSourceBatchSlicingTests
{
    [Fact]
    public async Task WhenANewSubjectHoldsANewChild_ThenTheFirstSliceCarriesBoth()
    {
        // Arrange: the new subject carries the context, so its child's assignment is captured before its own attach
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new CycleTestNode(context) { Name = "Root" };
        var mirror = CreateMirror(source);
        var parent = new CycleTestNode(context) { Name = "Parent" };
        var changes = CaptureChanges(context, () =>
        {
            parent.Child = new CycleTestNode { Name = "Child" };
            source.Child = parent;
        });

        // Act
        var slices = await WriteOneChangeAtATimeAsync(changes);
        Apply(source, mirror, slices[0]);

        // Assert
        Assert.Equal("Child", mirror.Child?.Child?.Name);
    }

    [Fact]
    public async Task WhenASubjectMovesBetweenParents_ThenTheMirrorKeepsItsInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var moved = new CycleTestNode { Name = "Moved" };
        var firstParent = new CycleTestNode { Name = "First", Items = [moved] };
        var secondParent = new CycleTestNode { Name = "Second" };
        var source = new CycleTestNode(context) { Name = "Root", Child = firstParent, Parent = secondParent };
        var mirror = CreateMirror(source);
        var mirroredMoved = Assert.Single(mirror.Child!.Items);
        var changes = CaptureChanges(context, () =>
        {
            firstParent.Items = [];
            secondParent.Items = [moved];
        });

        // Act
        foreach (var slice in await WriteOneChangeAtATimeAsync(changes))
        {
            Apply(source, mirror, slice);
        }

        // Assert
        Assert.Empty(mirror.Child.Items);
        Assert.Same(mirroredMoved, Assert.Single(mirror.Parent!.Items));
    }

    [Fact]
    public async Task WhenAPropertyChangesTwiceInReverseRevisionOrder_ThenTheNewestCommittedValueArrives()
    {
        // Arrange: concurrent writers can enqueue in the opposite order they committed
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new CycleTestNode(context) { Name = "A" };
        var mirror = CreateMirror(source);
        var property = new PropertyReference(source, nameof(CycleTestNode.Name));
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<string?>(property, ChangeOrigin.Local, timestamp, null, "B", "C", revision: 2),
            SubjectPropertyChange.Create<string?>(property, ChangeOrigin.Local, timestamp, null, "A", "B", revision: 1)
        ];

        // Act
        foreach (var slice in await WriteOneChangeAtATimeAsync(changes))
        {
            Apply(source, mirror, slice);
        }

        // Assert
        Assert.Equal("C", mirror.Name);
    }

    private static async Task<List<SubjectPropertyChange[]>> WriteOneChangeAtATimeAsync(SubjectPropertyChange[] changes)
    {
        var slices = new List<SubjectPropertyChange[]>();
        var source = new Mock<ISubjectSource>();
        source.Setup(s => s.WriteBatchSize).Returns(1);
        source
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> slice, CancellationToken _) =>
            {
                slices.Add(slice.ToArray());
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var result = await source.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);
        Assert.Null(result.Error);
        return slices;
    }

    private static CycleTestNode CreateMirror(CycleTestNode source)
    {
        var mirror = new CycleTestNode(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        return mirror;
    }

    private static void Apply(CycleTestNode source, CycleTestNode mirror, SubjectPropertyChange[] slice)
        => mirror.ApplySubjectUpdate(
            SubjectUpdate.CreatePartialUpdateFromChanges(source, slice, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

    private static SubjectPropertyChange[] CaptureChanges(IInterceptorSubjectContext context, Action change)
    {
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add))
        {
            change();
        }

        return changes.ToArray();
    }
}

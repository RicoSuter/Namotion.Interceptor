using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle.Acceptance;

/// <summary>
/// Defect class 7: a structural write must validate the value the terminal actually stored, not
/// only the value that was proposed, so a rewriting setter cannot install a subject the write never
/// saw. See <see cref="WriteProtocolAcceptance"/> for what each repro pins and what would turn it
/// green without the defect being fixed.
/// </summary>
/// <remarks>
/// On this branch the enforcement is a shape check rather than a validation: a structural property
/// that supplies no raw reader is refused outright, and one that supplies a raw reader is trusted
/// to store faithfully and never checked. Every repro here goes through the trusted shape, because
/// that is the shape every generated subject uses and the only one a real consumer reaches.
/// </remarks>
public class TerminalStoreValidationAcceptanceTests
{
    /// <summary>
    /// FAILS on this branch. Demonstrates defect 7 in its most serious form: a raw writer that
    /// substitutes a subject owned by another context commits with no exception at all. Observed
    /// symptom: <c>Record.Exception</c> returns null, the graph commits an edge to the proposed
    /// subject and attaches it to the writing context, while the backing field and the getter both
    /// expose the foreign subject the graph knows nothing about. Two subjects are corrupted by one
    /// write, and the foreign one keeps its own context, so the two contexts now disagree about a
    /// graph neither of them can see whole.
    /// </summary>
    [Fact]
    public void WhenARawWriterStoresASubjectOwnedByAnotherContext_ThenTheRejectedWriteLeavesTheGraphUnchanged()
    {
        // Arrange
        var context = AcceptanceContext.Create();
        var otherContext = AcceptanceContext.Create();
        var parent = new SubstitutingRawWriterDevice();
        parent.AttachToContext(context);
        var foreign = new SubstitutingRawWriterDevice();
        foreign.AttachToContext(otherContext);
        parent.Substitute = foreign;
        var proposed = new SubstitutingRawWriterDevice();

        // Act
        var exception = Record.Exception(() => parent.Child = proposed);

        // Assert
        Assert.NotNull(exception);
        var graph = AcceptanceContext.GetGraph(context);
        var property = new PropertyReference(parent, nameof(SubstitutingRawWriterDevice.Child));
        Assert.False(graph.ContainsOccurrence(property, foreign, 0),
            "the rejected write committed the terminal's foreign value as the property baseline");
        Assert.False(graph.ContainsOccurrence(property, proposed, 0),
            "the rejected write committed the proposed value the committed field does not hold");
        Assert.False(graph.IsOwned(foreign),
            "the rejected write recorded ownership for a subject owned by another context");
        Assert.Same(otherContext, foreign.TryGetContext());
        Assert.Empty(foreign.GetParents());
        Assert.Null(proposed.TryGetContext());
    }

    /// <summary>
    /// FAILS on this branch. The same defect with an unattached substitute, which is the exact
    /// normalizing-setter shape a device writes when it maps its input. Observed symptom: the write
    /// completes, the committed field holds the substitute and the substitute is attached to
    /// nothing, so the graph and the committed field disagree about the same property. The
    /// assertion is stated as agreement rather than as a specific outcome, so either answer
    /// (rejecting the write, or claiming what was stored) passes it.
    /// </summary>
    [Fact]
    public void WhenARawWriterStoresAnUnattachedSubstitute_ThenTheGraphAndTheCommittedFieldAgree()
    {
        // Arrange
        var context = AcceptanceContext.Create();
        var parent = new SubstitutingRawWriterDevice();
        parent.AttachToContext(context);
        var substitute = new SubstitutingRawWriterDevice();
        parent.Substitute = substitute;
        var proposed = new SubstitutingRawWriterDevice();

        // Act
        Record.Exception(() => parent.Child = proposed);

        // Assert
        var field = parent.RawChild;
        Assert.True(ReferenceEquals(field, substitute) == (substitute.TryGetContext() is not null),
            $"the substitute's attachment disagrees with the committed field (held={ReferenceEquals(field, substitute)}, " +
            $"attached={substitute.TryGetContext() is not null})");
        Assert.True(ReferenceEquals(field, proposed) == (proposed.TryGetContext() is not null),
            $"the proposed value's attachment disagrees with the committed field (held={ReferenceEquals(field, proposed)}, " +
            $"attached={proposed.TryGetContext() is not null})");
    }

    /// <summary>
    /// FAILS on this branch. The legal half of defect 7, and the case that stops an over-aggressive
    /// fix: a normalizing setter that stores a reordered subset of the proposed subjects must stay
    /// legal, because every stored subject was proposed and therefore already claimed. The stored
    /// list is a different instance from the proposed one, so a reference-equality short circuit
    /// cannot hide the rewrite. Observed symptom: the write succeeds and the surviving subjects are
    /// attached correctly, but the dropped subject stays attached to the writing context with an
    /// incoming edge, unreachable from the committed property. It is a phantom edge: the graph
    /// holds a subject the committed value no longer mentions.
    /// </summary>
    [Fact]
    public void WhenARawWriterReordersAndDropsTheProposedSubjects_ThenTheWriteSucceeds()
    {
        // Arrange
        var context = AcceptanceContext.Create();
        var parent = new ReorderingRawWriterDevice();
        parent.AttachToContext(context);
        var kept = new ReorderingRawWriterDevice { Name = "b" };
        var alsoKept = new ReorderingRawWriterDevice { Name = "a" };
        var dropped = new ReorderingRawWriterDevice { Name = "dropped" };

        // Act
        var exception = Record.Exception(() => parent.Children = [kept, alsoKept, dropped]);

        // Assert
        Assert.Null(exception);
        Assert.Equal(["a", "b"], parent.Children!.Select(child => child.Name));
        Assert.Same(context, kept.TryGetContext());
        Assert.Same(context, alsoKept.TryGetContext());
        Assert.Null(dropped.TryGetContext());
        Assert.Empty(dropped.GetParents());
    }

    /// <summary>
    /// PASSES on this branch. The value-typed second claim: the identical shape stored once as an
    /// <c>ImmutableArray</c> and once behind a reference-typed declaration must cost the same
    /// number of passes over its subjects, because a value type has no identity for the protocol to
    /// compare and a naive implementation therefore claims it twice.
    /// </summary>
    [Fact]
    public void WhenARawWriterStoresTheProposedImmutableArray_ThenItIsNotScannedAgain()
    {
        // Arrange
        var context = AcceptanceContext.Create();
        var device = new StoredValueClaimRawDevice();
        device.AttachToContext(context);
        var immutableChild = new ExecutorCountingSubject();
        var listChild = new ExecutorCountingSubject();

        // Act
        device.ImmutableChildren = [immutableChild];
        device.ListChildren = new List<ExecutorCountingSubject> { listChild };

        // Assert
        Assert.Equal(listChild.ExecutorAccessCount, immutableChild.ExecutorAccessCount);
    }

    /// <summary>
    /// FAILS on this branch. The stamp-equality hole in defect 7: a raw writer substitutes a
    /// value-typed collection that compares equal to the proposal by version stamp while holding an
    /// entirely different, foreign-owned subject. An implementation that lets the stored value's own
    /// equality decide whether it still needs claiming skips the check exactly here. Observed
    /// symptom: no exception is raised. The graph invariants below do hold, so the foreign subject
    /// is not adopted, but nothing tells the caller its write was not the write that landed.
    /// </summary>
    [Fact]
    public void WhenARawWriterStoresAnEquallyStampedValueHoldingAForeignSubject_ThenTheRejectedWriteLeavesTheGraphUnchanged()
    {
        // Arrange
        var context = AcceptanceContext.Create();
        var otherContext = AcceptanceContext.Create();
        var device = new StoredValueClaimRawDevice();
        device.AttachToContext(context);
        var foreign = new Person { FirstName = "F" };
        ((IInterceptorSubject)foreign).AttachToContext(otherContext);
        device.StampedSubstitute = new StampedChildren(7, [foreign]);

        // Act
        var exception = Record.Exception(() => device.Stamped = new StampedChildren(7, []));

        // Assert
        Assert.NotNull(exception);
        var graph = AcceptanceContext.GetGraph(context);
        Assert.False(graph.ContainsOccurrence(new PropertyReference(device, nameof(StoredValueClaimRawDevice.Stamped)), foreign, 0),
            "the rejected write committed a value the claim never validated as the property baseline");
        Assert.False(graph.IsOwned(foreign),
            "the rejected write recorded ownership for a subject owned by another context");
        Assert.Same(otherContext, ((IInterceptorSubject)foreign).TryGetContext());
        Assert.Empty(((IInterceptorSubject)foreign).GetParents());
    }
}

using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle.Acceptance;

/// <summary>
/// Defect class 10, the documented boundary rather than a defect: when a raw writer stores
/// something the write never proposed, the framework cannot restore the backing field, because the
/// only store it has is the terminal the subject handed it and a terminal that ignores its argument
/// re-stores the same value when replayed. What it can and must do is leave the graph untouched.
/// The boundary is asserted in both directions, so a change that moves it is seen rather than
/// absorbed.
/// </summary>
public class TerminalStoreFieldBoundaryAcceptanceTests
{
    /// <summary>
    /// FAILS on this branch, on the graph half only. Demonstrates that the boundary this branch
    /// documents in docs/design/tracking-lifecycle.md ("The graph is left untouched, but the backing
    /// field holds whatever that terminal stored") holds only for the field. Observed symptom: the
    /// field half passes, the graph half does not. The graph commits an occurrence for the proposed
    /// subject that the committed field does not hold and attaches it to the writing context, so the
    /// unrestorable field is no longer the boundary. The negative assertion on the field is
    /// deliberate: a change that starts restoring it fails this test, which is the point.
    /// </summary>
    [Fact]
    public void WhenARawWriterStoresAnUnproposedValue_ThenTheFieldKeepsItAndTheGraphIsUntouched()
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
        var graph = AcceptanceContext.GetGraph(context);
        var property = new PropertyReference(parent, nameof(SubstitutingRawWriterDevice.Child));

        // Act
        Record.Exception(() => parent.Child = proposed);

        // Assert: the field is not restored, which is the accepted half of the boundary.
        Assert.Same(foreign, parent.RawChild);

        // Assert: the graph is untouched, which is the half the boundary promises.
        Assert.False(graph.HasSnapshot(property),
            "the write committed a snapshot for a property whose stored value it never validated");
        Assert.False(graph.IsOwned(proposed),
            "the write recorded ownership for a subject the committed field does not hold");
        Assert.Null(proposed.TryGetContext());
        Assert.Empty(proposed.GetParents());
    }
}

using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The write protocol claims and validates the proposed component before the terminal runs, so a
/// terminal that stores a different graph stores subjects nothing validated. These pin which
/// terminal rewrites stay legal and which must be rejected before the property changes.
/// </summary>
public class TerminalStoreContractTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    /// <summary>
    /// Reproduces the finding that only the proposed value is claimed before the terminal runs, so
    /// a foreign-context subject stored by a normalizing terminal is discovered only by the
    /// authoritative getter reread, which happens after the backing field already changed.
    /// Reproduces on a single thread with no artificially held window.
    /// </summary>
    [Fact]
    public void WhenATerminalStoresASubjectOwnedByAnotherContext_ThenTheRejectedWriteLeavesTheFieldUnchanged()
    {
        // Arrange: the terminal substitutes a subject owned by a second context, which the claim
        // taken before the terminal never covered.
        var context = CreateContext();
        var otherContext = CreateContext();
        var parent = new SubstitutingDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var foreign = new SubstitutingDevice();
        ((IInterceptorSubject)foreign).AttachToContext(otherContext);
        parent.Substitute = foreign;

        // Act
        var exception = Record.Exception(() => parent.Child = new SubstitutingDevice());

        // Assert: the write is rejected, but it must be rejected before the backing field changed.
        Assert.NotNull(exception);
        Assert.True(parent.Child is null,
            "a rejected write must leave no changed field, but the terminal's foreign subject is " +
            "committed in the property and the rejection arrived only afterwards as " +
            $"{exception.GetType().Name}: {exception.Message}");
        Assert.Same(otherContext, ((IInterceptorSubject)foreign).TryGetContext());
        Assert.Empty(((IInterceptorSubject)foreign).GetParents());
    }

    /// <summary>
    /// Parity guard, not a repro: a normalizing setter that stores a reordered subset of the
    /// proposed subjects is legal and must keep working. This is also the only case that runs the
    /// stored-value scan on a value that has to pass.
    /// </summary>
    [Fact]
    public void WhenATerminalReordersAndDropsTheProposedSubjects_ThenTheWriteSucceeds()
    {
        // Arrange: the stored list is a different instance from the proposed one, so a
        // reference-equality short circuit cannot hide the rewrite.
        var context = CreateContext();
        var parent = new ReorderingDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var kept = new ReorderingDevice { Name = "b" };
        var alsoKept = new ReorderingDevice { Name = "a" };
        var dropped = new ReorderingDevice { Name = "dropped" };

        // Act
        parent.Children = [kept, alsoKept, dropped];

        // Assert: the kept subjects are owned in the terminal's order, not the caller's, and the
        // dropped one's provisional claim was handed back rather than becoming ownership.
        Assert.Equal(["a", "b"], parent.Children!.Select(child => child.Name));
        Assert.Same(context, ((IInterceptorSubject)kept).TryGetContext());
        Assert.Same(context, ((IInterceptorSubject)alsoKept).TryGetContext());
        Assert.Null(((IInterceptorSubject)dropped).TryGetContext());
        Assert.Empty(((IInterceptorSubject)dropped).GetParents());
    }
}

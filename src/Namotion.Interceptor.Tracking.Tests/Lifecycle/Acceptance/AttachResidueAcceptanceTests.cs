using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle.Acceptance;

/// <summary>
/// Defect class 6: an explicit attach that is refused must leave no residue behind, and the
/// exception explaining why it was refused must be the one the caller sees. The two residue repros
/// this class was written around are carried unchanged on this branch in
/// <c>AttachResidueTests</c>; what is repeated here is the half that branch answers differently.
/// </summary>
public class AttachResidueAcceptanceTests
{
    /// <summary>
    /// FAILS on this branch. Demonstrates the defect 6 variant: a lifecycle handler that refuses the
    /// child's attach makes the seed throw, and a handler that then also refuses the child's detach
    /// makes the cleanup throw on top of it. The reason the attach failed must survive the cleanup
    /// that ran after it, and the root must be left in a state the caller can still detach.
    /// Observed symptom: the first five assertions hold, so the attach exception is not masked by the
    /// cleanup and the root keeps its context and its explicit anchor. The last one fails: there is
    /// no rollback, so the publication stands, and the explicit detach that is the caller's remaining
    /// way out throws the refusing handler's own InvalidOperationException("the rollback was
    /// refused") instead of completing. The residue is therefore not merely left behind, it is
    /// unreachable.
    /// </summary>
    [Fact]
    public void WhenARollbackCallbackThrows_ThenTheAttachExceptionIsTheOneThatEscapes()
    {
        // Arrange: the handler refuses both directions, so the seed throws and so does the rollback.
        var child = new Person { FirstName = "C" };
        var context = AcceptanceContext.Create()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (!ReferenceEquals(change.Subject, child))
                {
                    return;
                }

                if (change.IsContextAttach)
                {
                    throw new InvalidOperationException("the attach was refused");
                }

                if (change.IsContextDetach)
                {
                    throw new InvalidOperationException("the rollback was refused");
                }
            }), _ => false);

        var holder = new EnumerableChildrenHolder { Children = new List<Person> { child } };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));

        // Assert: the reason the attach failed survives the rollback that ran after it.
        Assert.NotNull(exception);
        Assert.Contains("the attach was refused", exception.Message);
        Assert.DoesNotContain("the rollback was refused", exception.ToString());

        // The rollback stopped where it stood, so what it had not undone is still published; the
        // root keeps the anchor and claim that make that cleanable.
        Assert.NotNull(((IInterceptorSubject)holder).TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)holder).Executor.AttachmentAnchor);
        Assert.Null(Record.Exception(() => ((IInterceptorSubject)holder).DetachFromContext(context)));
    }
}

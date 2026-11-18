using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// A commit whose structural write makes lifecycle handlers write under the lock: the queued work is drained by the
/// commit's apply write.
/// </summary>
public class TransactionCommitQueuedWorkTests
{
    [Fact]
    public async Task WhenCommitAppliesAStructuralWriteWhoseHandlerWrites_ThenTheChangesAreDeliveredInWriteOrderAndTheDerivedPropertyOnce()
    {
        // Arrange
        var (context, root, mother) = CreateWithAttachHandlerThatWritesFirstName();
        root.LastName = "Doe";

        var delivered = new List<(string Name, object? NewValue)>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root)
            {
                delivered.Add((change.Property.Name, change.GetNewValue<object?>()));
            }
        });

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            root.Mother = mother;
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Same(mother, root.Mother);
        Assert.Equal("h", root.FirstName);
        Assert.Equal(
            [nameof(Person.Mother), nameof(Person.FirstName), nameof(Person.FullName)],
            delivered.Select(change => change.Name).Where(name => name is nameof(Person.Mother) or nameof(Person.FirstName) or nameof(Person.FullName)));
        Assert.Equal("h Doe", Assert.Single(delivered, change => change.Name == nameof(Person.FullName)).NewValue);
    }

    [Fact]
    public async Task WhenObserverOfAHandlerWriteThrowsDuringTheCommit_ThenTheCommitReportsItAndTheChangesStay()
    {
        // Arrange
        var (context, root, mother) = CreateWithAttachHandlerThatWritesFirstName();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root && change.Property.Name == nameof(Person.FirstName))
            {
                throw new InvalidOperationException("observer of the handler write");
            }
        });

        // BestEffort keeps a change whose apply threw after its value landed (#674); Rollback would revert it.
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        root.Mother = mother;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Equal("observer of the handler write", Assert.IsType<InvalidOperationException>(Assert.Single(exception.Errors)).Message);
        Assert.Equal(nameof(Person.Mother), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.Same(mother, root.Mother);
        Assert.Equal("h", root.FirstName);
    }

    private static (IInterceptorSubjectContext Context, Person Root, Person Mother) CreateWithAttachHandlerThatWritesFirstName()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var root = new Person(context);
        var mother = new Person();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == mother)
            {
                root.FirstName = "h";
            }
        }));

        return (context, root, mother);
    }
}

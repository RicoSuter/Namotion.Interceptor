using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for transaction lifecycle: creation, disposal, commit behavior.
/// </summary>
public class SubjectTransactionLifecycleTests : TransactionTestBase
{
    [Fact]
    public async Task WhenTransactionBegun_ThenItBecomesCurrent()
    {
        // Arrange
        var context = CreateContext();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Assert
        Assert.NotNull(transaction);
        Assert.Same(transaction, SubjectTransaction.Current);
    }

    [Fact]
    public async Task WhenDisposedWithoutCommit_ThenPendingChangesAreDiscarded()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        SubjectTransaction? capturedTransaction;

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            capturedTransaction = transaction;
            person.FirstName = "John";

            Assert.NotNull(SubjectTransaction.Current);
            Assert.Single(transaction.GetPendingChanges());
        }

        // Assert
        Assert.Null(SubjectTransaction.Current);
        Assert.Empty(capturedTransaction.GetPendingChanges());
        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task WhenCommitted_ThenChangesAreAppliedToModel()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";

        Assert.Single(transaction.GetPendingChanges());

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(person.FirstName);
        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task WhenCommitted_ThenPendingChangesAreCleared()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        SubjectTransaction capturedTransaction;

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            capturedTransaction = transaction;
            person.FirstName = "John";

            Assert.Single(transaction.GetPendingChanges());

            await transaction.CommitAsync(CancellationToken.None);

            Assert.Empty(transaction.GetPendingChanges());
        }

        // Assert
        Assert.Null(SubjectTransaction.Current);
        Assert.Empty(capturedTransaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenUsingBlockEnds_ThenCurrentTransactionIsCleared()
    {
        // Arrange
        var context = CreateContext();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            Assert.NotNull(SubjectTransaction.Current);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public void WhenContextCreated_ThenTransactionInterceptorPrecedesObservable()
    {
        // Arrange
        var context = CreateContext();

        // Act
        var writeInterceptors = context.GetServices<Interceptors.IWriteInterceptor>().ToList();
        var transactionInterceptor = writeInterceptors.OfType<SubjectTransactionInterceptor>().FirstOrDefault();

        // Assert
        Assert.NotNull(transactionInterceptor);

        var types = writeInterceptors.Select(i => i.GetType().Name).ToList();
        var transactionIndex = types.IndexOf("SubjectTransactionInterceptor");
        var observableIndex = types.IndexOf("PropertyChangeObservable");
        Assert.True(transactionIndex < observableIndex,
            $"Transaction interceptor should be before PropertyChangeObservable. Order: {string.Join(" -> ", types)}");
    }
}

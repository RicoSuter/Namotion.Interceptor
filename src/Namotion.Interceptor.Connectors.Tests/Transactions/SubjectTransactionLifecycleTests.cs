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
    public async Task BeginTransaction_CreatesActiveTransaction()
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
    public async Task Dispose_CleansUpWithoutCommit_ImplicitRollback()
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
            Assert.Single(transaction.PendingChanges);
        }

        // Assert
        Assert.Null(SubjectTransaction.Current);
        Assert.Empty(capturedTransaction.PendingChanges);
        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task BeginTransaction_WhenNested_ThrowsInvalidOperationException()
    {
        // Arrange
        var context = CreateContext();
        using var transaction1 = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort));

        // Assert
        Assert.Contains("Nested transactions are not supported", exception.Message);
    }

    [Fact]
    public async Task CommitAsync_WithNoChanges_ReturnsImmediately()
    {
        // Arrange
        var context = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Empty(transaction.PendingChanges);
    }

    [Fact]
    public async Task CommitAsync_AppliesChangesToModel()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";

        Assert.Single(transaction.PendingChanges);

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(person.FirstName);
        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_CleansUpPendingChanges()
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

            Assert.Single(transaction.PendingChanges);

            await transaction.CommitAsync(CancellationToken.None);

            Assert.Empty(transaction.PendingChanges);
        }

        // Assert
        Assert.Null(SubjectTransaction.Current);
        Assert.Empty(capturedTransaction.PendingChanges);
    }

    [Fact]
    public async Task CommitAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var context = CreateContext();
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        transaction.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await transaction.CommitAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CommitAsync_WhenCalledTwice_ThrowsInvalidOperationException()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);

            // Act
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transaction.CommitAsync(CancellationToken.None));

            // Assert
            Assert.Contains("already been committed", exception.Message);
        }
    }

    [Fact]
    public async Task Dispose_CalledMultipleTimes_IsIdempotent()
    {
        // Arrange
        var context = CreateContext();
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        Assert.NotNull(SubjectTransaction.Current);

        // Act
        transaction.Dispose();
        Assert.Null(SubjectTransaction.Current);

        transaction.Dispose();

        // Assert
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public async Task AsyncLocalBehavior_CurrentClearedAfterUsingBlock()
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
    public void InterceptorRegistration_TransactionBeforeObservable()
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

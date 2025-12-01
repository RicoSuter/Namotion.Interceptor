using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Sources.Transactions;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

/// <summary>
/// Tests for transaction lifecycle: creation, disposal, commit behavior.
/// </summary>
public class SubjectTransactionLifecycleTests : TransactionTestBase
{
    [Fact]
    public void BeginTransaction_CreatesActiveTransaction()
    {
        CreateContext();

        using var transaction = SubjectTransaction.BeginTransaction();

        Assert.NotNull(transaction);
        Assert.Same(transaction, SubjectTransaction.Current);
    }

    [Fact]
    public void Dispose_CleansUpWithoutCommit_ImplicitRollback()
    {
        var context = CreateContext();
        var person = new Person(context);

        SubjectTransaction? capturedTransaction;
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            capturedTransaction = transaction;
            person.FirstName = "John";

            Assert.NotNull(SubjectTransaction.Current);
            Assert.Single(transaction.PendingChanges);
        }

        Assert.Null(SubjectTransaction.Current);
        Assert.Empty(capturedTransaction.PendingChanges);
        Assert.Null(person.FirstName);
    }

    [Fact]
    public void BeginTransaction_WhenNested_ThrowsInvalidOperationException()
    {
        CreateContext();
        using var transaction1 = SubjectTransaction.BeginTransaction();

        var exception = Assert.Throws<InvalidOperationException>(() => SubjectTransaction.BeginTransaction());
        Assert.Contains("Nested transactions are not supported", exception.Message);
    }

    [Fact]
    public async Task CommitAsync_WithNoChanges_ReturnsImmediately()
    {
        CreateContext();
        using var transaction = SubjectTransaction.BeginTransaction();

        await transaction.CommitAsync(CancellationToken.None);

        Assert.Empty(transaction.PendingChanges);
    }

    [Fact]
    public async Task CommitAsync_AppliesChangesToModel()
    {
        var context = CreateContext();
        var person = new Person(context);

        using var transaction = SubjectTransaction.BeginTransaction();
        person.FirstName = "John";

        Assert.Single(transaction.PendingChanges);

        await transaction.CommitAsync(CancellationToken.None);

        Assert.NotNull(person.FirstName);
        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_CleansUpPendingChanges()
    {
        var context = CreateContext();
        var person = new Person(context);
        SubjectTransaction capturedTransaction;

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            capturedTransaction = transaction;
            person.FirstName = "John";

            Assert.Single(transaction.PendingChanges);

            await transaction.CommitAsync(CancellationToken.None);

            Assert.Empty(transaction.PendingChanges);
        }

        Assert.Null(SubjectTransaction.Current);
        Assert.Empty(capturedTransaction.PendingChanges);
    }

    [Fact]
    public async Task CommitAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        CreateContext();
        var transaction = SubjectTransaction.BeginTransaction();
        transaction.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await transaction.CommitAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CommitAsync_WhenCalledTwice_ThrowsInvalidOperationException()
    {
        var context = CreateContext();
        var person = new Person(context);

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transaction.CommitAsync(CancellationToken.None));

            Assert.Contains("already been committed", exception.Message);
        }
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_IsIdempotent()
    {
        var transaction = SubjectTransaction.BeginTransaction();
        Assert.NotNull(SubjectTransaction.Current);

        transaction.Dispose();
        Assert.Null(SubjectTransaction.Current);

        transaction.Dispose();
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public async Task AsyncLocalBehavior_CurrentClearedAfterUsingBlock()
    {
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            Assert.NotNull(SubjectTransaction.Current);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public void InterceptorRegistration_TransactionBeforeObservable()
    {
        var context = CreateContext();

        var writeInterceptors = context.GetServices<Interceptors.IWriteInterceptor>().ToList();
        var transactionInterceptor = writeInterceptors.OfType<SubjectTransactionInterceptor>().FirstOrDefault();

        Assert.NotNull(transactionInterceptor);

        var types = writeInterceptors.Select(i => i.GetType().Name).ToList();
        var transactionIndex = types.IndexOf("SubjectTransactionInterceptor");
        var observableIndex = types.IndexOf("PropertyChangeObservable");
        Assert.True(transactionIndex < observableIndex,
            $"Transaction interceptor should be before PropertyChangeObservable. Order: {string.Join(" -> ", types)}");
    }
}

using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Additional tests covering edge cases identified in the PR review.
/// </summary>
public class SubjectTransactionAdditionalTests : TransactionTestBase
{
    [Fact]
    public async Task WhenCommitTimeoutIsInfinite_ThenSlowSourceCommitSucceeds()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var delayedSource = CreateDelayedSource(TimeSpan.FromMilliseconds(100));
        person.GetPropertyReference(nameof(Person.FirstName)).SetSource(delayedSource.Object);

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            commitTimeout: Timeout.InfiniteTimeSpan);

        person.FirstName = "John";

        // Act - should not timeout
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("John", person.FirstName);
        AssertTransactionSucceeded(transaction);
    }

    [Fact]
    public async Task WhenNoTimeoutSpecified_ThenDefaultCommitTimeoutIs30Seconds()
    {
        // Arrange & Assert
        // Verify the default timeout constant is 30 seconds
        Assert.Equal(TimeSpan.FromSeconds(30), SubjectTransactionExtensions.DefaultCommitTimeout);

        // Act - verify transaction is created successfully with defaults
        var context = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Assert
        Assert.NotNull(transaction);
        Assert.Same(transaction, SubjectTransaction.Current);
    }

    [Fact]
    public async Task WhenBeginCalledWithCancelledToken_ThenThrows()
    {
        // Arrange
        var context = CreateContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel the token

        // Act & Assert - attempting to begin a transaction with a cancelled token should throw
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                locking: TransactionLocking.Exclusive,
                cancellationToken: cts.Token);
        });
    }

    [Fact]
    public async Task WhenTransactionBeginsAndEnds_ThenHasActiveTransactionTracksIt()
    {
        // Arrange
        var context = CreateContext();

        // Note: We cannot assert HasActiveTransaction is false initially because
        // other tests may be running in parallel with active transactions.
        // We only verify that THIS execution context has no transaction.
        Assert.Null(SubjectTransaction.Current);

        // Act - start transaction
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            // Assert - transaction is active in this context
            Assert.True(SubjectTransaction.HasActiveTransaction);
            Assert.NotNull(SubjectTransaction.Current);
            Assert.Same(transaction, SubjectTransaction.Current);
        }

        // Assert - transaction is no longer active in this context
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public async Task WhenSomeChangesApplied_ThenIsPartialSuccessIsTrue()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var source = CreatePartialFailureSource(change => change.Property.Name == "FirstName");

        person.GetPropertyReference(nameof(Person.FirstName)).SetSource(source.Object);
        person.GetPropertyReference(nameof(Person.LastName)).SetSource(source.Object);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            async () => await transaction.CommitAsync(CancellationToken.None));

        // LastName should have succeeded, FirstName should have failed
        Assert.NotEmpty(exception.AppliedChanges);
        Assert.NotEmpty(exception.FailedChanges);
        Assert.True(exception.IsPartialSuccess);
    }

    [Fact]
    public void WhenWriteResultIsPartialFailure_ThenItIsDistinctFromFullFailure()
    {
        // Arrange - create a dummy change for testing
        var dummyChanges = new SubjectPropertyChange[1];

        // Act - Full failure
        var fullFailure = WriteResult.Failure(dummyChanges.AsMemory(), new Exception("Full"));

        // Act - Partial failure
        var partialFailure = WriteResult.PartialFailure(dummyChanges.AsMemory(), new Exception("Partial"));

        // Assert
        Assert.False(fullFailure.IsPartialFailure);
        Assert.True(partialFailure.IsPartialFailure);
        Assert.False(fullFailure.IsFullySuccessful);
        Assert.False(partialFailure.IsFullySuccessful);
    }

    [Fact]
    public void WhenWriteResultSuccessReused_ThenBothValuesAreFullySuccessful()
    {
        // Act
        var success1 = WriteResult.Success;
        var success2 = WriteResult.Success;

        // Assert - both should be the same singleton value (empty ImmutableArray)
        Assert.True(success1.IsFullySuccessful);
        Assert.True(success2.IsFullySuccessful);
        Assert.False(success1.IsPartialFailure);
        Assert.False(success2.IsPartialFailure);
        Assert.Empty(success1.FailedChanges);
        Assert.Empty(success2.FailedChanges);
    }
}

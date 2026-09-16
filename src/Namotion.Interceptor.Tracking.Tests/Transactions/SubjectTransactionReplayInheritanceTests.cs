using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectTransactionReplayInheritanceTests
{
    [Theory]
    [InlineData(TransactionLocking.Exclusive)]
    [InlineData(TransactionLocking.Optimistic)]
    public async Task WhenTransactionWritesOwnAndInheritedProperties_ThenBothCommit(TransactionLocking locking)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var subject = new TransactionReplayLeaf(context) { RootValue = 1, Value = 1 };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback, locking: locking);
        subject.RootValue = 2;
        subject.Value = 3;

        // Act
        await transaction.CommitAsync(default);

        // Assert
        Assert.Equal(2, subject.RootValue);
        Assert.Equal(3, subject.Value);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Theory]
    [InlineData(TransactionFailureHandling.Rollback, 1)]
    [InlineData(TransactionFailureHandling.BestEffort, 2)]
    public async Task WhenDerivedChangedHookThrows_ThenItsMutationIsCompensated(TransactionFailureHandling policy, int expectedRootValue)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var subject = new TransactionReplayLeaf(context) { RootValue = 1, Value = 1 };
        using var transaction = await context.BeginTransactionAsync(policy);
        subject.RootValue = 2;
        subject.Value = 3;
        subject.ThrowOnChanged = true;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(default).AsTask());

        // Assert
        Assert.Equal(expectedRootValue, subject.RootValue);
        Assert.Equal(1, subject.Value);
        Assert.Single(exception.FailedChanges, change => change.Property.Name == nameof(subject.Value));
        Assert.Empty(transaction.GetPendingChanges());
    }
}

[InterceptorSubject]
public partial class TransactionReplayRoot
{
    public partial int RootValue { get; set; }
}

public class TransactionReplayBridge : TransactionReplayRoot;

[InterceptorSubject]
public partial class TransactionReplayLeaf : TransactionReplayBridge
{
    public partial int Value { get; set; }

    public bool ThrowOnChanged { get; set; }

    partial void OnValueChanged(int newValue)
    {
        if (ThrowOnChanged && newValue == 3)
        {
            throw new InvalidOperationException("Derived changed hook failed after assignment.");
        }
    }
}

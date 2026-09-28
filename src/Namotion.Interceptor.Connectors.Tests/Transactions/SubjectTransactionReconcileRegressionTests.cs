using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Regression tests for transaction commit reconcile paths that were previously untested:
/// the combined source-write-failure + apply-failure branch under BestEffort, and the
/// optimistic lock-acquire failure leaving the transaction retryable.
/// </summary>
public class SubjectTransactionReconcileRegressionTests : TransactionTestBase
{
    [Fact]
    public async Task WhenSourceWriteAndAnotherApplyFailInBestEffortMode_ThenSuccessIsKeptAndOnlyAppliedThenFailedIsReverted()
    {
        // Arrange - Three source-bound properties so the reconcile path sees BOTH a source-write
        // failure (failedSource.Count > 0) AND a local apply failure (applyFailed.Count > 0)
        // at the same time under BestEffort:
        //   A = person.FirstName : bound to a source whose write FAILS (apply would be fine).
        //   B = device.PropertyB : bound to a source whose write SUCCEEDS, but OnSet THROWS on apply.
        //   C = person.LastName  : bound to a source whose write succeeds and applies fine.
        var context = CreateContext();
        var person = new Person(context);
        var device = new ThrowingDevice(context)
        {
            ShouldThrow = prop => prop == nameof(ThrowingDevice.PropertyB)
        };

        // Source for A: write fails for FirstName.
        var failingSource = CreateFailingSource("FirstName write failed");

        // Source for B and C: writes succeed; record per-property writes to detect reverts.
        var successWrites = new List<(string Property, object? Value)>();
        var successSource = new Mock<ISubjectSource>();
        successSource.Setup(s => s.WriteBatchSize).Returns(0);
        successSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    successWrites.Add((change.Property.Metadata.Name!, change.GetNewValue<object?>()));
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(failingSource.Object); // A
        new PropertyReference(device, nameof(ThrowingDevice.PropertyB)).SetSource(successSource.Object); // B
        new PropertyReference(person, nameof(Person.LastName)).SetSource(successSource.Object); // C

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John"; // A: source write fails
        device.PropertyB = true;   // B: source write succeeds, OnSet throws on apply
        person.LastName = "Doe";   // C: source write succeeds, applies fine

        device.ThrowingEnabled = true;

        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert - exception reports the partial outcome per the BestEffort matrix.
        // AppliedChanges contains only C; not A (source failed) and not B (apply failed).
        Assert.Single(exception.AppliedChanges);
        Assert.Equal(nameof(Person.LastName), exception.AppliedChanges[0].Property.Metadata.Name);

        // FailedChanges contains BOTH A (source write failed) and B (apply failed).
        Assert.Equal(2, exception.FailedChanges.Count);
        Assert.Contains(exception.FailedChanges, c => c.Property.Metadata.Name == nameof(Person.FirstName));
        Assert.Contains(exception.FailedChanges, c => c.Property.Metadata.Name == nameof(ThrowingDevice.PropertyB));

        // Model state: C applied; A and B kept at their original (unset) values.
        Assert.Equal("Doe", person.LastName); // C applied
        Assert.Null(person.FirstName);          // A not applied (source failed)
        Assert.False(device.PropertyB);         // B not applied (apply threw)

        // B's source write was REVERTED (written true, then inverse false) so source == model.
        Assert.Contains((nameof(ThrowingDevice.PropertyB), (object?)true), successWrites);
        Assert.Contains((nameof(ThrowingDevice.PropertyB), (object?)false), successWrites);

        // C is not reverted: LastName written exactly once with "Doe" and never inverted.
        Assert.Single(successWrites, w => w.Property == nameof(Person.LastName));
        Assert.Equal((object?)"Doe", successWrites.Single(w => w.Property == nameof(Person.LastName)).Value);

        // A's source was NOT reverted: the failing source only ever saw the forward write attempt.
        failingSource.Verify(s => s.WriteChangesAsync(
            It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenOptimisticLockAcquireCancelled_ThenTransactionRemainsRetryable()
    {
        // Arrange - Context with WithTransactions() but NOT WithSourceTransactions(): no writer, so
        // commit goes through the local path where the optimistic lock is acquired at commit time
        // using the passed cancellation token. A cancelled token makes the lock acquire throw, which must
        // reset _commitStarted so a retry is possible (the fix in CommitLocalAfterLockAsync).
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions(); // No WithSourceTransactions()

        var person = new Person(context);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic);

        person.FirstName = "John";

        // Act & Assert - the first commit fails because the lock acquire observes the cancelled token.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transaction.CommitAsync(cts.Token).AsTask());

        // A retry with a live token must succeed: the transaction must NOT be wedged with
        // "CommitAsync is already in progress."
        await transaction.CommitAsync(CancellationToken.None);

        // Assert - the change was applied on the successful retry.
        Assert.Equal("John", person.FirstName);
    }
}

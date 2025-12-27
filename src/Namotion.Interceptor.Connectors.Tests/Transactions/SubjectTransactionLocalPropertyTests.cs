using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for local property failure handling during transaction commits.
/// Local properties are those without an external source, where OnSet* methods can throw.
/// </summary>
public class SubjectTransactionLocalPropertyTests : TransactionTestBase
{
    [Fact]
    public async Task BestEffortMode_LocalPropertyThrows_AppliesSuccessfulChanges()
    {
        // Arrange - PropertyB throws during commit, PropertyA succeeds
        var context = CreateContext();
        var device = new ThrowingDevice(context)
        {
            ShouldThrow = prop => prop == nameof(ThrowingDevice.PropertyB)
        };

        // Act - capture changes (throwing disabled during capture)
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        device.PropertyA = true;
        device.PropertyB = true;

        // Enable throwing just before commit
        device.ThrowingEnabled = true;

        var exception = await Assert.ThrowsAsync<TransactionException>(() => transaction.CommitAsync(CancellationToken.None));

        // Assert - PropertyA should be applied, PropertyB should fail
        Assert.True(device.PropertyA);
        Assert.False(device.PropertyB); // Not applied due to exception
        Assert.Single(exception.FailedChanges);
        Assert.Equal(nameof(ThrowingDevice.PropertyB), exception.FailedChanges[0].Property.Metadata.Name);
    }

    [Fact]
    public async Task RollbackMode_LocalPropertyThrows_RevertsAllChanges()
    {
        // Arrange - PropertyB throws during commit, PropertyA succeeds initially
        var context = CreateContext();
        var device = new ThrowingDevice(context)
        {
            ShouldThrow = prop => prop == nameof(ThrowingDevice.PropertyB)
        };

        // Act - capture changes (throwing disabled during capture)
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        device.PropertyA = true;
        device.PropertyB = true;

        // Enable throwing just before commit
        device.ThrowingEnabled = true;

        var exception = await Assert.ThrowsAsync<TransactionException>(() => transaction.CommitAsync(CancellationToken.None));

        // Assert - Both should be reverted (PropertyA was applied then rolled back)
        Assert.False(device.PropertyA); // Rolled back
        Assert.False(device.PropertyB); // Never applied
        Assert.Contains("Rollback was attempted", exception.Message);
    }

    [Fact]
    public async Task RollbackMode_LocalPropertyThrows_RevertsExternalSources()
    {
        // Arrange - FirstName has external source, PropertyB (local) throws during commit
        var context = CreateContext();
        var person = new Person(context);
        var device = new ThrowingDevice(context)
        {
            ShouldThrow = prop => prop == nameof(ThrowingDevice.PropertyB)
        };

        var writeCount = 0;
        var successSource = new Mock<ISubjectSource>();
        successSource.Setup(s => s.WriteBatchSize).Returns(0);
        successSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback(() => writeCount++)
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);

        // Act - capture changes (throwing disabled during capture)
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";  // External source - succeeds
        device.PropertyA = true;     // Local - succeeds
        device.PropertyB = true;     // Local - throws during commit

        // Enable throwing just before commit
        device.ThrowingEnabled = true;

        var exception = await Assert.ThrowsAsync<TransactionException>(() => transaction.CommitAsync(CancellationToken.None));

        // Assert
        // External source should have been written and then reverted (2 calls)
        Assert.Equal(2, writeCount);
        Assert.Null(person.FirstName); // Reverted
        Assert.False(device.PropertyA); // Rolled back
        Assert.False(device.PropertyB); // Never applied
    }

    [Fact]
    public async Task RollbackMode_LocalPropertyRevertThrows_ReportsMultipleFailures()
    {
        // Arrange - PropertyA throws on revert (when setting back to false)
        var revertPhase = false;
        var context = CreateContext();
        var device = new ThrowingDevice(context);

        device.ShouldThrow = prop =>
        {
            if (prop == nameof(ThrowingDevice.PropertyB))
                return true; // Always throw on PropertyB during commit
            if (prop == nameof(ThrowingDevice.PropertyA) && revertPhase)
                return true; // Throw on PropertyA during revert
            return false;
        };

        // Act - capture changes (throwing disabled)
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        device.PropertyA = true;  // Will succeed initially, then fail on revert
        device.PropertyB = true;  // Will throw during commit

        // Enable throwing for commit phase
        device.ThrowingEnabled = true;

        // The revert phase will be triggered after PropertyB fails
        // PropertyA's revert will also fail
        revertPhase = true;

        var exception = await Assert.ThrowsAsync<TransactionException>(() => transaction.CommitAsync(CancellationToken.None));

        // Assert - Both PropertyA (revert failure) and PropertyB (initial failure) should be reported
        Assert.Equal(2, exception.FailedChanges.Count);
        Assert.Equal(2, exception.Errors.Count);
    }

    [Fact]
    public async Task NoWriter_BestEffortMode_LocalPropertyThrows_AppliesSuccessfulChanges()
    {
        // Arrange - Context without WithSourceTransactions(), only WithTransactions()
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions(); // No WithSourceTransactions()

        var device = new ThrowingDevice(context)
        {
            ShouldThrow = prop => prop == nameof(ThrowingDevice.PropertyB)
        };

        // Act - capture changes (throwing disabled)
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        device.PropertyA = true;
        device.PropertyB = true;

        // Enable throwing just before commit
        device.ThrowingEnabled = true;

        var exception = await Assert.ThrowsAsync<TransactionException>(() => transaction.CommitAsync(CancellationToken.None));

        // Assert
        Assert.True(device.PropertyA); // Applied
        Assert.False(device.PropertyB); // Failed
        Assert.Single(exception.FailedChanges);
    }

    [Fact]
    public async Task NoWriter_RollbackMode_LocalPropertyThrows_RevertsSuccessfulChanges()
    {
        // Arrange - Context without WithSourceTransactions(), only WithTransactions()
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions(); // No WithSourceTransactions()

        var device = new ThrowingDevice(context)
        {
            ShouldThrow = prop => prop == nameof(ThrowingDevice.PropertyB)
        };

        // Act - capture changes (throwing disabled)
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        device.PropertyA = true;
        device.PropertyB = true;

        // Enable throwing just before commit
        device.ThrowingEnabled = true;

        var exception = await Assert.ThrowsAsync<TransactionException>(() => transaction.CommitAsync(CancellationToken.None));

        // Assert - PropertyA should be rolled back
        Assert.False(device.PropertyA); // Rolled back
        Assert.False(device.PropertyB); // Never applied
        Assert.Contains("Rollback was attempted", exception.Message);
    }

    [Fact]
    public async Task AllLocalPropertiesSucceed_NoException()
    {
        // Arrange - No properties throw
        var context = CreateContext();
        var device = new ThrowingDevice(context)
        {
            ShouldThrow = _ => false
        };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        device.PropertyA = true;
        device.PropertyB = true;

        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.True(device.PropertyA);
        Assert.True(device.PropertyB);
    }

    [Fact]
    public async Task MixedSourceAndLocal_SourceFails_LocalNotApplied_InRollbackMode()
    {
        // Arrange - External source fails, local should not be applied in rollback mode
        var context = CreateContext();
        var person = new Person(context);
        var device = new ThrowingDevice(context)
        {
            ShouldThrow = _ => false
        };

        var failSource = CreateFailingSource();
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(failSource.Object);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";  // External source - fails
        device.PropertyA = true;     // Local - would succeed but shouldn't be applied

        var exception = await Assert.ThrowsAsync<TransactionException>(() => transaction.CommitAsync(CancellationToken.None));

        // Assert - Local properties should not be applied when external source fails in rollback mode
        Assert.Null(person.FirstName);
        Assert.False(device.PropertyA); // Not applied due to rollback
    }

    [Fact]
    public async Task StagedExecution_ExternalSourcesWrittenBeforeLocalProperties()
    {
        // Arrange - Verify that external sources are written BEFORE local OnSet* methods are called
        var executionOrder = new List<string>();
        var context = CreateContext();
        var person = new Person(context);
        var device = new ThrowingDevice(context);

        // Track when external source write happens
        var successSource = new Mock<ISubjectSource>();
        successSource.Setup(s => s.WriteBatchSize).Returns(0);
        successSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                executionOrder.Add("ExternalSource");
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);

        // Track when local OnSet* is called - only during commit, not during capture
        device.ShouldThrow = prop =>
        {
            executionOrder.Add($"LocalOnSet:{prop}");
            return false; // Don't actually throw
        };
        // Note: ThrowingEnabled stays false during capture, enabled just before commit

        // Act - capture changes (ThrowingEnabled is false, so ShouldThrow is not called)
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";  // External source
        device.PropertyA = true;     // Local property - captured but OnSet* doesn't record yet

        // Enable tracking just before commit
        device.ThrowingEnabled = true;

        await transaction.CommitAsync(CancellationToken.None);

        // Assert - External source must be written before local OnSet* is called during commit
        Assert.Equal(2, executionOrder.Count);
        Assert.Equal("ExternalSource", executionOrder[0]);
        Assert.Equal("LocalOnSet:PropertyA", executionOrder[1]);
    }

    [Fact]
    public async Task StagedExecution_SourceBoundPropertyOnSetThrows_RevertsExternalSources()
    {
        // Arrange - Tests Stage 3 failure: source-bound property's OnSet* throws after external write succeeds
        // This is different from local property failure - it's when applying source-bound values to in-process model fails
        var context = CreateContext();
        var device = new ThrowingDevice(context);

        var writeCount = 0;
        var successSource = new Mock<ISubjectSource>();
        successSource.Setup(s => s.WriteBatchSize).Returns(0);
        successSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback(() => writeCount++)
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success));

        // Bind PropertyA to external source - it will throw during in-process apply (Stage 3)
        new PropertyReference(device, nameof(ThrowingDevice.PropertyA)).SetSource(successSource.Object);

        device.ShouldThrow = prop => prop == nameof(ThrowingDevice.PropertyA);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        device.PropertyA = true;  // Source-bound, OnSet* will throw during Stage 3

        device.ThrowingEnabled = true;

        var exception = await Assert.ThrowsAsync<TransactionException>(() => transaction.CommitAsync(CancellationToken.None));

        // Assert - External source was written and then reverted (2 calls)
        Assert.Equal(2, writeCount);
        Assert.False(device.PropertyA); // Not applied due to failure + rollback
        Assert.Contains("Rollback was attempted", exception.Message);
    }
}

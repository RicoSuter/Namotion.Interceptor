using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for transaction modes: BestEffort and Rollback.
/// </summary>
public class SubjectTransactionFailureHandlingTests : TransactionTestBase
{
    [Fact]
    public async Task BestEffortMode_AppliesSuccessfulChanges_WhenSomeSourcesFail()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var successSource = CreateSucceedingSource();
        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Null(person.LastName);
        Assert.Single(ex.FailedChanges);
    }

    [Fact]
    public async Task RollbackMode_AppliesAllChanges_WhenAllSourcesSucceed()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task RollbackMode_RevertsSuccessfulSources_WhenAnySourceFails()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var writeCallCount = 0;
        var successSource = new Mock<ISubjectSource>();
        successSource.Setup(s => s.WriteBatchSize).Returns(0);
        successSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback(() => writeCallCount++)
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success));

        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        Assert.Equal(2, writeCallCount); // Initial write + revert
        Assert.Contains("Rollback was attempted", ex.Message);
    }

    [Fact]
    public async Task RollbackMode_ReportsRevertFailures_WhenRevertAlsoFails()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var callCount = 0;
        var successThenFailSource = new Mock<ISubjectSource>();
        successThenFailSource.Setup(s => s.WriteBatchSize).Returns(0);
        successThenFailSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1)
                    return new ValueTask<WriteResult>(WriteResult.Success); // Initial write succeeds
                else
                    return new ValueTask<WriteResult>(WriteResult.Failure(new InvalidOperationException("Revert failed"))); // Revert fails
            });

        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successThenFailSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        Assert.Equal(2, ex.FailedChanges.Count);
    }

    [Fact]
    public async Task RollbackMode_ChangesWithoutSource_RemainSuccessful_WhenSourceFails()
    {
        // Arrange
        // Tests that changes without source (NullSource) are always successful,
        // even in Rollback mode when source writes fail.
        // In SourceTransactionWriter, changes with NullSource are excluded from rollback.
        var context = CreateContext();
        var person = new Person(context);

        var failSource = CreateFailingSource();

        // Only FirstName has a source, LastName has no source
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(failSource.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe"; // No source - should be in successful changes

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        // LastName should NOT be applied in Rollback mode (shouldApplyChanges is false)
        // because the rollback mode check is at SubjectTransaction level, not SourceTransactionWriter
        // However, the successful changes returned from SourceTransactionWriter should include LastName
        Assert.Single(ex.FailedChanges);
        Assert.Single(ex.AppliedChanges); // LastName is in successful changes (written to "NullSource")
        Assert.Equal(nameof(Person.LastName), ex.AppliedChanges[0].Property.Metadata.Name);

        // Nothing applied in rollback mode
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
    }

    [Fact]
    public async Task BestEffortMode_ChangesWithoutSource_AlwaysApplied_WhenSourceFails()
    {
        // Arrange
        // Tests that changes without source are applied in BestEffort mode
        var context = CreateContext();
        var person = new Person(context);

        var failSource = CreateFailingSource();

        // Only FirstName has a source
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(failSource.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe"; // No source

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        // In BestEffort, successful changes are applied
        Assert.Equal("Doe", person.LastName);
        Assert.Null(person.FirstName);
        Assert.Single(ex.FailedChanges);
    }
}

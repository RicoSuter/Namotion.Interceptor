using Moq;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Sources.Transactions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

/// <summary>
/// Tests for transaction modes: BestEffort and Rollback.
/// </summary>
public class SubjectTransactionModeTests : TransactionTestBase
{
    [Fact]
    public async Task BestEffortMode_AppliesSuccessfulChanges_WhenSomeSourcesFail()
    {
        var context = CreateContext();
        var person = new Person(context);

        var successSource = CreateSucceedingSource();
        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        using var tx = SubjectTransaction.BeginTransaction(TransactionMode.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        Assert.Equal("John", person.FirstName);
        Assert.Null(person.LastName);
        Assert.Single(ex.FailedChanges);
    }

    [Fact]
    public async Task RollbackMode_AppliesAllChanges_WhenAllSourcesSucceed()
    {
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        using var tx = SubjectTransaction.BeginTransaction(TransactionMode.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task RollbackMode_RevertsSuccessfulSources_WhenAnySourceFails()
    {
        var context = CreateContext();
        var person = new Person(context);

        var writeCallCount = 0;
        var successSource = new Mock<ISubjectSource>();
        successSource.Setup(s => s.WriteBatchSize).Returns(0);
        successSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback(() => writeCallCount++)
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success()));

        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        using var tx = SubjectTransaction.BeginTransaction(TransactionMode.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        Assert.Equal(2, writeCallCount); // Initial write + revert
        Assert.Contains("Rollback was attempted", ex.Message);
    }

    [Fact]
    public async Task RollbackMode_ReportsRevertFailures_WhenRevertAlsoFails()
    {
        var context = CreateContext();
        var person = new Person(context);

        var callCount = 0;
        var successThenFailSource = new Mock<ISubjectSource>();
        successThenFailSource.Setup(s => s.WriteBatchSize).Returns(0);
        successThenFailSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1)
                    return new ValueTask<WriteResult>(WriteResult.Success()); // Initial write succeeds
                else
                    return new ValueTask<WriteResult>(WriteResult.Failure(new InvalidOperationException("Revert failed"))); // Revert fails
            });

        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successThenFailSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        using var tx = SubjectTransaction.BeginTransaction(TransactionMode.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        Assert.Equal(2, ex.FailedChanges.Count);
    }
}

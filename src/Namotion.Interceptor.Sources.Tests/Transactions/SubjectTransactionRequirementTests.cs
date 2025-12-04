using Moq;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Sources.Transactions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

/// <summary>
/// Tests for transaction requirements: None and SingleWrite.
/// </summary>
public class SubjectTransactionRequirementTests : TransactionTestBase
{
    [Fact]
    public async Task SingleWriteRequirement_ThrowsException_WhenMultipleSources()
    {
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<AggregateException>(() => tx.CommitAsync());

        var innerEx = Assert.Single(ex.InnerExceptions);
        Assert.IsType<InvalidOperationException>(innerEx);
        Assert.Contains("2 sources", innerEx.Message);
        Assert.Contains("only 1 is allowed", innerEx.Message);

        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);

        source1.Verify(s => s.WriteChangesAsync(
            It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        source2.Verify(s => s.WriteChangesAsync(
            It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SingleWriteRequirement_ThrowsException_WhenChangesExceedBatchSize()
    {
        var context = CreateContext();
        var person = new Person(context);

        var source = CreateSourceWithBatchSize(1); // Only 1 change per batch

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe"; // 2 changes > batch size of 1

        var ex = await Assert.ThrowsAsync<AggregateException>(() => tx.CommitAsync());

        var innerEx = Assert.Single(ex.InnerExceptions);
        Assert.IsType<InvalidOperationException>(innerEx);
        Assert.Contains("2 changes", innerEx.Message);
        Assert.Contains("WriteBatchSize is 1", innerEx.Message);

        source.Verify(s => s.WriteChangesAsync(
            It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SingleWriteRequirement_Succeeds_WhenRequirementsMet()
    {
        var context = CreateContext();
        var person = new Person(context);

        var source = CreateSourceWithBatchSize(10); // Enough for 2 changes

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);

        source.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SingleWriteRequirement_Succeeds_WithUnlimitedBatchSize()
    {
        var context = CreateContext();
        var person = new Person(context);

        var source = CreateSucceedingSource(); // WriteBatchSize = 0 (unlimited)

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task SingleWriteRequirement_AllowsChangesWithoutSource()
    {
        var context = CreateContext();
        var person = new Person(context);

        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task SingleWriteRequirement_AllowsMixedSourceAndNoSource()
    {
        var context = CreateContext();
        var person = new Person(context);

        var source = CreateSucceedingSource();
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);

        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe"; // No source - always allowed

        await tx.CommitAsync();

        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Theory]
    [InlineData(true)]  // Explicit TransactionRequirement.None
    [InlineData(false)] // Default (no requirement specified)
    public async Task NoneRequirement_AllowsMultipleSources(bool explicitRequirement)
    {
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        using var tx = explicitRequirement
            ? SubjectTransaction.BeginTransaction(TransactionMode.Rollback, TransactionRequirement.None)
            : SubjectTransaction.BeginTransaction();
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task SingleWriteWithRollback_RevertsOnFailure_WithSingleSource()
    {
        var context = CreateContext();
        var person = new Person(context);

        var source = new Mock<ISubjectSource>();
        source.Setup(s => s.WriteBatchSize).Returns(10);
        source.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Failure(new InvalidOperationException("Write failed"))));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<AggregateException>(() => tx.CommitAsync());

        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        Assert.Single(ex.InnerExceptions);
    }
}

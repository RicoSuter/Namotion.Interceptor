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

        using var tx = await context.BeginExclusiveTransactionAsync(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        Assert.Single(ex.FailedChanges);
        Assert.IsType<InvalidOperationException>(ex.FailedChanges[0].Error);
        Assert.Contains("2 sources", ex.FailedChanges[0].Error.Message);
        Assert.Contains("only 1 is allowed", ex.FailedChanges[0].Error.Message);

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

        using var tx = await context.BeginExclusiveTransactionAsync(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe"; // 2 changes > batch size of 1

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        Assert.Single(ex.FailedChanges);
        Assert.IsType<InvalidOperationException>(ex.FailedChanges[0].Error);
        Assert.Contains("2 changes", ex.FailedChanges[0].Error.Message);
        Assert.Contains("WriteBatchSize is 1", ex.FailedChanges[0].Error.Message);

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

        using var tx = await context.BeginExclusiveTransactionAsync(
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

        using var tx = await context.BeginExclusiveTransactionAsync(
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

        using var tx = await context.BeginExclusiveTransactionAsync(
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

        using var tx = await context.BeginExclusiveTransactionAsync(
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
            ? await context.BeginExclusiveTransactionAsync(TransactionMode.Rollback, TransactionRequirement.None)
            : await context.BeginExclusiveTransactionAsync();
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

        using var tx = await context.BeginExclusiveTransactionAsync(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        // Each failed property change is reported separately
        Assert.Equal(2, ex.FailedChanges.Count);
    }

    [Fact]
    public async Task SingleWriteRequirement_ValidationFailure_ReturnsChangesWithoutSourceAsSuccessful()
    {
        // Tests that when SingleWrite validation fails (multiple sources),
        // changes without source are still returned as successful in the result.
        // This tests SourceTransactionWriter line 29-31.
        var context = CreateContext();
        var person = new Person(context);
        var father = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        // Two properties with different sources (violates SingleWrite)
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);
        // Father has no source

        using var tx = await context.BeginExclusiveTransactionAsync(
            TransactionMode.BestEffort, // Use BestEffort to see successful changes applied
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";
        person.Father = father; // No source - should be in successful changes

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        // Validation error should be reported
        Assert.Single(ex.FailedChanges);
        Assert.Contains("2 sources", ex.FailedChanges[0].Error.Message);

        // Father (change without source) should be in successful changes
        Assert.Single(ex.AppliedChanges);
        Assert.Equal(nameof(Person.Father), ex.AppliedChanges[0].Property.Metadata.Name);

        // In BestEffort mode, successful changes are applied
        Assert.Same(father, person.Father);

        // Source properties should NOT be applied (validation failed before any writes)
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);

        // Verify no source writes were attempted
        source1.Verify(s => s.WriteChangesAsync(
            It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        source2.Verify(s => s.WriteChangesAsync(
            It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SingleWriteRequirement_BatchSizeViolation_ReturnsChangesWithoutSourceAsSuccessful()
    {
        // Tests that when SingleWrite validation fails due to batch size,
        // changes without source are still returned as successful.
        var context = CreateContext();
        var person = new Person(context);
        var father = new Person(context);

        var source = CreateSourceWithBatchSize(1); // Only 1 change per batch

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);
        // Father has no source

        using var tx = await context.BeginExclusiveTransactionAsync(
            TransactionMode.BestEffort,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe"; // 2 changes > batch size of 1
        person.Father = father; // No source

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        // Validation error for batch size
        Assert.Single(ex.FailedChanges);
        Assert.Contains("WriteBatchSize is 1", ex.FailedChanges[0].Error.Message);

        // Father should be in successful changes
        Assert.Single(ex.AppliedChanges);
        Assert.Equal(nameof(Person.Father), ex.AppliedChanges[0].Property.Metadata.Name);

        // In BestEffort, successful changes are applied
        Assert.Same(father, person.Father);
    }
}

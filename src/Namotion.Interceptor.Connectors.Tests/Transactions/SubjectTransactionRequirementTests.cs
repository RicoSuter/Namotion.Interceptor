using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for transaction requirements: None and SingleWrite.
/// </summary>
public class SubjectTransactionRequirementTests : TransactionTestBase
{
    [Fact]
    public async Task SingleWriteRequirement_ThrowsException_WhenMultipleSources()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback,
            requirement: TransactionRequirement.SingleWrite);
      
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        Assert.Equal(2, ex.FailedChanges.Count); // Both changes failed (validation error)
        Assert.Single(ex.Errors);
        var error = Assert.IsType<InvalidOperationException>(ex.Errors[0]);
        Assert.Contains("2 sources", error.Message);
        Assert.Contains("only 1 is allowed", error.Message);

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
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source = CreateSourceWithBatchSize(1); // Only 1 change per batch

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback,
            requirement: TransactionRequirement.SingleWrite);
       
        person.FirstName = "John";
        person.LastName = "Doe"; // 2 changes > batch size of 1

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        Assert.Equal(2, ex.FailedChanges.Count); // Both changes failed (validation error)
        Assert.Single(ex.Errors);
        var error = Assert.IsType<InvalidOperationException>(ex.Errors[0]);
        Assert.Contains("2 changes", error.Message);
        Assert.Contains("WriteBatchSize is 1", error.Message);

        source.Verify(s => s.WriteChangesAsync(
            It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SingleWriteRequirement_Succeeds_WhenRequirementsMet()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source = CreateSourceWithBatchSize(10); // Enough for 2 changes

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback,
            requirement: TransactionRequirement.SingleWrite);
        
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);

        source.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SingleWriteRequirement_Succeeds_WithUnlimitedBatchSize()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source = CreateSucceedingSource(); // WriteBatchSize = 0 (unlimited)

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback,
            requirement: TransactionRequirement.SingleWrite);
       
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task SingleWriteRequirement_AllowsChangesWithoutSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback,
            requirement: TransactionRequirement.SingleWrite);
     
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task SingleWriteRequirement_AllowsMixedSourceAndNoSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source = CreateSucceedingSource();
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback,
            requirement: TransactionRequirement.SingleWrite);
      
        person.FirstName = "John";
        person.LastName = "Doe"; // No source - always allowed

        await tx.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Theory]
    [InlineData(true)]  // Explicit TransactionRequirement.None
    [InlineData(false)] // Default (no requirement specified)
    public async Task NoneRequirement_AllowsMultipleSources(bool explicitRequirement)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        // Act
        using var tx = explicitRequirement
            ? await context.BeginTransactionAsync(TransactionFailureHandling.Rollback, requirement: TransactionRequirement.None)
            : await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
       
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task SingleWriteWithRollback_RevertsOnFailure_WithSingleSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source = new Mock<ISubjectSource>();
        source.Setup(s => s.WriteBatchSize).Returns(10);
        source.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(changes, new InvalidOperationException("Write failed"))));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback,
            requirement: TransactionRequirement.SingleWrite);
      
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        // Each failed property change is reported separately
        Assert.Equal(2, ex.FailedChanges.Count);
    }

    [Fact]
    public async Task SingleWriteRequirement_ValidationFailure_ReturnsChangesWithoutSourceAsSuccessful()
    {
        // Arrange
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

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, // Use BestEffort to see successful changes applied
            requirement: TransactionRequirement.SingleWrite);
        
        person.FirstName = "John";
        person.LastName = "Doe";
        person.Father = father; // No source - should be in successful changes

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        // Validation error should be reported - both source-bound changes failed
        Assert.Equal(2, ex.FailedChanges.Count);
        Assert.Single(ex.Errors);
        var error = Assert.IsType<InvalidOperationException>(ex.Errors[0]);
        Assert.Contains("2 sources", error.Message);

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
        // Arrange
        // Tests that when SingleWrite validation fails due to batch size,
        // changes without source are still returned as successful.
        var context = CreateContext();
        var person = new Person(context);
        var father = new Person(context);

        var source = CreateSourceWithBatchSize(1); // Only 1 change per batch

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);
        // Father has no source

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            requirement: TransactionRequirement.SingleWrite);
      
        person.FirstName = "John";
        person.LastName = "Doe"; // 2 changes > batch size of 1
        person.Father = father; // No source

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        // Validation error for batch size - both source-bound changes failed
        Assert.Equal(2, ex.FailedChanges.Count);
        Assert.Single(ex.Errors);
        var error = Assert.IsType<InvalidOperationException>(ex.Errors[0]);
        Assert.Contains("WriteBatchSize is 1", error.Message);

        // Father should be in successful changes
        Assert.Single(ex.AppliedChanges);
        Assert.Equal(nameof(Person.Father), ex.AppliedChanges[0].Property.Metadata.Name);

        // In BestEffort, successful changes are applied
        Assert.Same(father, person.Father);
    }
}

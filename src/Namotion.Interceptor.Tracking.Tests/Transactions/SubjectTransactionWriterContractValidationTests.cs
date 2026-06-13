using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

[CollectionDefinition(WriterContractValidationCollection.Name, DisableParallelization = true)]
public sealed class WriterContractValidationCollection
{
    public const string Name = "WriterContractValidation";
}

/// <summary>
/// Toggles the process-wide <see cref="SubjectTransaction.ValidateWriterContract"/> switch, so the
/// collection disables parallelization to keep the toggle out of concurrently running tests.
/// </summary>
[Collection(WriterContractValidationCollection.Name)]
public class SubjectTransactionWriterContractValidationTests
{
    private static IInterceptorSubjectContext CreateTransactionContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
    }

    /// <summary>
    /// Violates the in-place marking contract: instead of only marking the source, it overwrites the first
    /// snapshot slot with a change for a DIFFERENT property, which the opt-in contract validation must catch.
    /// </summary>
    private sealed class SlotCorruptingTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            var span = changes.Span;
            // Overwrite slot 0 with the change that belongs to slot 1, so slot 0 now holds a different property.
            span[0] = span[1];
            return new ValueTask<SourceWriteResult>(new SourceWriteResult([], [], [], RevertState: null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    [Fact]
    public async Task WhenWriterCorruptsSnapshotSlotAndValidationIsEnabled_ThenCommitFailsTerminally()
    {
        // Arrange
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(new SlotCorruptingTransactionWriter());

        var person = new Person(context);

        SubjectTransaction.ValidateWriterContract = true;
        try
        {
            using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
            person.FirstName = "John";
            person.LastName = "Doe";

            // Act & Assert
            var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Contains(exception.Errors, error =>
                error is InvalidOperationException && error.Message.Contains("contract"));

            // The violation is detected after source writes already happened and nothing was reverted,
            // so the transaction must be terminal: a retry would write to the sources a second time.
            var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Contains("already been committed", retryException.Message);
        }
        finally
        {
            SubjectTransaction.ValidateWriterContract = false;
        }
    }

    [Fact]
    public async Task WhenWriterCorruptsSnapshotSlotAndValidationIsDisabled_ThenCommitSucceeds()
    {
        // Arrange. Validation is off by default (opt-in diagnostic), so the corruption goes undetected.
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(new SlotCorruptingTransactionWriter());

        var person = new Person(context);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert: the commit completed and applied the corrupted snapshot as-is. Slot 0 was overwritten
        // with the LastName change, so FirstName was never applied; this pins that disabled validation
        // means undetected corruption, which is why the docs require enabling it during writer development.
        Assert.Null(person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }
}

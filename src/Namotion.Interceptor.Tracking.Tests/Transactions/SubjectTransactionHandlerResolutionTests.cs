using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Covers handlers registered on the context of a written subject rather than on the transaction's context.
/// </summary>
public class SubjectTransactionHandlerResolutionTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
    }

    [Fact]
    public async Task WhenHandlerIsRegisteredOnlyOnSubjectContextReachingTransactionsThroughFallback_ThenItTakesPart()
    {
        // Arrange
        var handler = new RecordingHandler();
        var probe = new CommitWriteProbe(handler);
        var transactionContext = CreateContext();
        var subjectContext = InterceptorSubjectContext.Create();
        subjectContext.AddService<ITransactionHandler>(handler);
        subjectContext.AddService(probe);
        subjectContext.AddFallbackContext(transactionContext);
        var person = new Person(subjectContext);
        probe.Writes.Clear();

        SubjectPropertyChange[]? changesSeenByHandler = null;
        handler.OnCommittingAction = (_, changes) => changesSeenByHandler = changes;

        // Act
        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Bob", Assert.Single(handler.Captured).Value);
        Assert.Equal(1, handler.CommittingCalls);
        var change = Assert.Single(changesSeenByHandler!);
        Assert.Equal(nameof(Person.FirstName), change.Property.Name);
        Assert.Equal("Bob", change.GetNewValue<string>());
        Assert.True(Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName)).Consumed);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenHandlerIsRegisteredOnSubjectContextAndTransactionContext_ThenItIsCalledOncePerEvent()
    {
        // Arrange
        var handler = new RecordingHandler();
        var transactionContext = CreateContext();
        transactionContext.AddService<ITransactionHandler>(handler);
        var subjectContext = InterceptorSubjectContext.Create();
        subjectContext.AddService<ITransactionHandler>(handler);
        subjectContext.AddFallbackContext(transactionContext);
        var person = new Person(subjectContext);

        // Act
        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Single(handler.Captured);
        Assert.Equal(1, handler.CommittingCalls);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenHandlerIsIncludedFromSubjectContext_ThenItSeesLaterWritesToOtherSubjects()
    {
        // Arrange
        var handler = new RecordingHandler();
        var transactionContext = CreateContext();
        var subjectContext = InterceptorSubjectContext.Create();
        subjectContext.AddService<ITransactionHandler>(handler);
        subjectContext.AddFallbackContext(transactionContext);
        var person = new Person(subjectContext);
        var otherPerson = new Person(transactionContext);

        SubjectPropertyChange[]? changesSeenByHandler = null;
        handler.OnCommittingAction = (_, changes) => changesSeenByHandler = changes;

        // Act
        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        otherPerson.FirstName = "Ann";
        person.FirstName = "Bob";
        otherPerson.LastName = "Lee";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(new object?[] { "Bob", "Lee" }, handler.Captured.Select(captured => captured.Value));
        Assert.Equal(3, changesSeenByHandler!.Length);
    }

    [Fact]
    public async Task WhenHandlerIsIncludedBeforeRetryOfFailedCommit_ThenItChecksTheRetry()
    {
        // Arrange
        var rejectCommit = true;
        var rejectingHandler = new RecordingHandler
        {
            OnCommittingAction = (_, _) =>
            {
                if (rejectCommit)
                {
                    throw new InvalidOperationException("rejected");
                }
            }
        };
        var includedHandler = new RecordingHandler();
        var transactionContext = CreateContext();
        transactionContext.AddService<ITransactionHandler>(rejectingHandler);
        var subjectContext = InterceptorSubjectContext.Create();
        subjectContext.AddService<ITransactionHandler>(includedHandler);
        subjectContext.AddFallbackContext(transactionContext);
        var person = new Person(subjectContext);
        var otherPerson = new Person(transactionContext);

        SubjectPropertyChange[]? changesSeenByIncludedHandler = null;
        includedHandler.OnCommittingAction = (_, changes) => changesSeenByIncludedHandler = changes;

        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        otherPerson.FirstName = "Ann";
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        person.FirstName = "Bob";
        rejectCommit = false;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, includedHandler.CommittingCalls);
        Assert.Equal(2, changesSeenByIncludedHandler!.Length);
        Assert.Equal("Ann", otherPerson.FirstName);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenAnotherThreadWritesSubjectWithOwnHandlerDuringOnCommitting_ThenWriteThrowsAndHandlerDoesNotTakePart()
    {
        // Arrange
        var checkingHandler = new RecordingHandler();
        var subjectHandler = new RecordingHandler();
        var transactionContext = CreateContext();
        transactionContext.AddService<ITransactionHandler>(checkingHandler);
        var subjectContext = InterceptorSubjectContext.Create();
        subjectContext.AddService<ITransactionHandler>(subjectHandler);
        subjectContext.AddFallbackContext(transactionContext);
        var person = new Person(subjectContext);
        var otherPerson = new Person(transactionContext);

        Exception? concurrentWriteException = null;
        checkingHandler.OnCommittingAction = (_, _) =>
        {
            // A started thread inherits the execution context, so it carries the transaction.
            var thread = new Thread(() => concurrentWriteException = Record.Exception(() => person.FirstName = "Bob"));
            thread.Start();
            thread.Join();
        };

        // Act
        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        otherPerson.FirstName = "Ann";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(concurrentWriteException);
        Assert.Contains("while transaction handlers check the commit", exception.Message);
        Assert.Empty(subjectHandler.Captured);
        Assert.Equal(0, subjectHandler.CommittingCalls);
        Assert.Null(person.FirstName);
        Assert.Equal("Ann", otherPerson.FirstName);
    }
}

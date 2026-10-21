using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;
using static Namotion.Interceptor.Tracking.Tests.Transactions.TransactionTestContext;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Covers handlers registered on the context of a written subject rather than on the transaction's context.
/// </summary>
public class SubjectTransactionHandlerResolutionTests
{
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
        var transactionContext = CreateContext(out var handler);
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
    public async Task WhenEqualHandlersAreRegisteredOnSubjectContextAndTransactionContext_ThenOnlyTheFirstTakesPart()
    {
        // Arrange
        var transactionContextHandler = new TypeEqualHandler();
        var subjectContextHandler = new TypeEqualHandler();
        var transactionContext = CreateContext();
        transactionContext.AddService<ITransactionHandler>(transactionContextHandler);
        var subjectContext = InterceptorSubjectContext.Create();
        subjectContext.AddService<ITransactionHandler>(subjectContextHandler);
        subjectContext.AddFallbackContext(transactionContext);
        var person = new Person(subjectContext);

        // Act
        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, transactionContextHandler.CommittingCalls);
        Assert.Equal(0, subjectContextHandler.CommittingCalls);
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
    public async Task WhenHandlerIsAddedToFallbackOfWrittenSubjectContextDuringTransaction_ThenNextWriteIncludesIt()
    {
        // Arrange
        var handler = new RecordingHandler();
        var transactionContext = CreateContext();
        var handlerContext = InterceptorSubjectContext.Create();
        var subjectContext = InterceptorSubjectContext.Create();
        subjectContext.AddService(new object());
        subjectContext.AddFallbackContext(transactionContext);
        subjectContext.AddFallbackContext(handlerContext);
        var person = new Person(subjectContext);

        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";

        // Act
        handlerContext.AddService<ITransactionHandler>(handler);
        person.LastName = "Lee";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(nameof(Person.LastName), Assert.Single(handler.Captured).Property.Name);
        Assert.Equal(1, handler.CommittingCalls);
        Assert.Equal("Lee", person.LastName);
    }

    [Fact]
    public async Task WhenHandlerIsIncludedBeforeRetryOfFailedCommit_ThenItChecksTheRetry()
    {
        // Arrange
        var rejectCommit = true;
        var transactionContext = CreateContext(out var rejectingHandler);
        rejectingHandler.OnCommittingAction = (_, _) =>
        {
            if (rejectCommit)
            {
                throw new InvalidOperationException("rejected");
            }
        };
        var includedHandler = new RecordingHandler();
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
        var transactionContext = CreateContext(out var checkingHandler);
        var subjectHandler = new RecordingHandler();
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
        Assert.Contains("commit is in progress", exception.Message);
        Assert.Empty(subjectHandler.Captured);
        Assert.Equal(0, subjectHandler.CommittingCalls);
        Assert.Null(person.FirstName);
        Assert.Equal("Ann", otherPerson.FirstName);
    }

    private sealed class TypeEqualHandler : ITransactionHandler
    {
        public int CommittingCalls { get; private set; }

        public void OnCapturing<TProperty>(SubjectTransaction transaction, PropertyReference property, TProperty newValue, ChangeOrigin origin)
        {
        }

        public void OnCommitting(SubjectTransaction transaction, ReadOnlySpan<SubjectPropertyChange> changes)
        {
            CommittingCalls++;
        }

        public override bool Equals(object? obj) => obj is TypeEqualHandler;

        public override int GetHashCode() => typeof(TypeEqualHandler).GetHashCode();
    }
}

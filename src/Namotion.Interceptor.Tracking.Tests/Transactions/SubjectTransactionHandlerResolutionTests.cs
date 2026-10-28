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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenEqualHandlersAreRegisteredOnTwoContexts_ThenOnlyTheFirstTakesPartOncePerEvent(bool firstOnTransactionContext)
    {
        // Arrange
        var firstHandler = new TypeEqualHandler();
        var secondHandler = new TypeEqualHandler();
        var transactionContext = CreateContext();
        var firstContext = firstOnTransactionContext ? transactionContext : CreateSubjectContext(transactionContext);
        firstContext.AddService<ITransactionHandler>(firstHandler);
        var first = new Person(firstContext);
        var second = new Person(CreateSubjectContext(transactionContext, secondHandler));

        // Act
        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        first.FirstName = "Ann";
        second.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, firstHandler.CapturingCalls);
        Assert.Equal(1, firstHandler.CommittingCalls);
        Assert.Equal(0, secondHandler.CapturingCalls);
        Assert.Equal(0, secondHandler.CommittingCalls);
        Assert.Equal("Bob", second.FirstName);
    }

    [Fact]
    public async Task WhenHandlerIsRegisteredOnlyOnSubjectContext_ThenItTakesPartFromTheFirstWriteToItsSubjects()
    {
        // Arrange
        var handler = new RecordingHandler();
        var probe = new CommitWriteProbe(handler);
        var transactionContext = CreateContext();
        var subjectContext = CreateSubjectContext(transactionContext, handler);
        subjectContext.AddService(probe);
        var person = new Person(subjectContext);
        var otherPerson = new Person(transactionContext);
        probe.Writes.Clear();

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
        Assert.Equal(1, handler.CommittingCalls);
        Assert.Equal(["Ann", "Bob", "Lee"], changesSeenByHandler!.Select(change => change.GetNewValue<string>()));
        Assert.True(Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName)).Consumed);
        Assert.Equal("Bob", person.FirstName);
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
        var transactionContext = CreateContext(out var rejectingHandler);
        rejectingHandler.RejectCommit = true;
        var includedHandler = new RecordingHandler();
        var person = new Person(CreateSubjectContext(transactionContext, includedHandler));
        var otherPerson = new Person(transactionContext);

        SubjectPropertyChange[]? changesSeenByIncludedHandler = null;
        includedHandler.OnCommittingAction = (_, changes) => changesSeenByIncludedHandler = changes;

        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        otherPerson.FirstName = "Ann";
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        person.FirstName = "Bob";
        rejectingHandler.RejectCommit = false;
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
        var person = new Person(CreateSubjectContext(transactionContext, subjectHandler));
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
        public int CapturingCalls { get; private set; }

        public int CommittingCalls { get; private set; }

        public void OnCapturing<TProperty>(in TransactionCaptureContext<TProperty> context)
        {
            CapturingCalls++;
        }

        public void OnCommitting(in TransactionCheckContext context)
        {
            CommittingCalls++;
        }

        public override bool Equals(object? obj) => obj is TypeEqualHandler;

        public override int GetHashCode() => typeof(TypeEqualHandler).GetHashCode();
    }
}

using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;
using static Namotion.Interceptor.Tracking.Tests.Transactions.TransactionTestContext;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class CommitWriteMarkerTests
{
    private static (IInterceptorSubjectContext Context, ITransactionHandler Handler, Person Person) CreateContextWithPerson()
    {
        var context = CreateContext(out var handler);
        return (context, handler, new Person(context));
    }

    [Fact]
    public async Task WhenValueDiffers_ThenMarkDoesNotMatchAndStaysSet()
    {
        // Arrange
        var (context, handler, person) = CreateContextWithPerson();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        using (CommitWriteMarker.Set(transaction, property, "Bob"))
        {
            // Act
            var changedValue = SubjectTransaction.TryConsumeCommitWrite(handler, property, "Changed");
            var checkedValue = SubjectTransaction.TryConsumeCommitWrite(handler, property, "Bob");

            // Assert
            Assert.False(changedValue);
            Assert.True(checkedValue);
        }
    }

    [Fact]
    public async Task WhenPropertyDiffers_ThenMarkDoesNotMatchAndStaysSet()
    {
        // Arrange
        var (context, handler, person) = CreateContextWithPerson();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var markedProperty = new PropertyReference(person, nameof(Person.FirstName));
        var otherProperty = new PropertyReference(person, nameof(Person.LastName));

        using (CommitWriteMarker.Set(transaction, markedProperty, "Bob"))
        {
            // Act
            var otherMatches = SubjectTransaction.TryConsumeCommitWrite(handler, otherProperty, "Bob");
            var markedMatches = SubjectTransaction.TryConsumeCommitWrite(handler, markedProperty, "Bob");

            // Assert
            Assert.False(otherMatches);
            Assert.True(markedMatches);
        }
    }

    [Fact]
    public async Task WhenMarkedValueIsNullOrNullableValueType_ThenOnlyEqualValueOrNullMatches()
    {
        // Arrange
        var (context, handler, person) = CreateContextWithPerson();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        bool equalValueMatches;
        using (CommitWriteMarker.Set(transaction, property, (int?)5))
        {
            equalValueMatches = SubjectTransaction.TryConsumeCommitWrite<int?>(handler, property, 5);
        }

        bool otherValueMatches;
        using (CommitWriteMarker.Set(transaction, property, (int?)5))
        {
            otherValueMatches = SubjectTransaction.TryConsumeCommitWrite<int?>(handler, property, 6);
        }

        bool nullMatches;
        using (CommitWriteMarker.Set(transaction, property, null))
        {
            nullMatches = SubjectTransaction.TryConsumeCommitWrite<int?>(handler, property, null);
        }

        bool valueMatchesNullMark;
        using (CommitWriteMarker.Set(transaction, property, null))
        {
            valueMatchesNullMark = SubjectTransaction.TryConsumeCommitWrite<int?>(handler, property, 5);
        }

        bool nullReferenceMatches;
        using (CommitWriteMarker.Set(transaction, property, null))
        {
            nullReferenceMatches = SubjectTransaction.TryConsumeCommitWrite<string?>(handler, property, null);
        }

        bool referenceMatchesNullMark;
        using (CommitWriteMarker.Set(transaction, property, null))
        {
            referenceMatchesNullMark = SubjectTransaction.TryConsumeCommitWrite(handler, property, "Bob");
        }

        // Assert
        Assert.True(equalValueMatches);
        Assert.False(otherValueMatches);
        Assert.True(nullMatches);
        Assert.False(valueMatchesNullMark);
        Assert.True(nullReferenceMatches);
        Assert.False(referenceMatchesNullMark);
    }

    [Fact]
    public async Task WhenScopeIsDisposedWithoutConsumption_ThenNothingMatches()
    {
        // Arrange
        var (context, handler, person) = CreateContextWithPerson();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        using (CommitWriteMarker.Set(transaction, property, "Bob"))
        {
            // Leave the mark unconsumed so disposal is the only operation that can clear it.
        }

        // Act
        var matches = SubjectTransaction.TryConsumeCommitWrite(handler, property, "Bob");

        // Assert
        Assert.False(matches);
    }

    [Fact]
    public async Task WhenNestedScopeIsDisposed_ThenOuterMarkIsRestored()
    {
        // Arrange
        var (context, handler, person) = CreateContextWithPerson();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var outerProperty = new PropertyReference(person, nameof(Person.FirstName));
        var innerProperty = new PropertyReference(person, nameof(Person.LastName));

        using (CommitWriteMarker.Set(transaction, outerProperty, "outer"))
        {
            using (CommitWriteMarker.Set(transaction, innerProperty, "inner"))
            {
                SubjectTransaction.TryConsumeCommitWrite(handler, innerProperty, "inner");
            }

            // Act
            var matches = SubjectTransaction.TryConsumeCommitWrite(handler, outerProperty, "outer");

            // Assert
            Assert.True(matches);
        }
    }

    [Fact]
    public async Task WhenHandlerIsNotPartOfTransaction_ThenCommitWriteIsNotConsumedForIt()
    {
        // Arrange
        var context = CreateContext(out _);
        var probe = new CommitWriteProbe(new RecordingHandler());
        context.AddService(probe);
        var person = new Person(context);
        probe.Writes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.False(Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName)).Consumed);
    }

    [Fact]
    public async Task WhenTransactionIsDisposedFromAnotherFlowDuringApply_ThenCommitWriteIsNotConsumed()
    {
        // Arrange
        // Keeps a transaction active in the process after the dispose, so the result does not depend on other tests.
        using var unrelatedTransaction = await BeginInOwnFlowAsync(CreateContext(out _));
        var context = CreateContext(out var handler);
        var disposer = new DisposingWriteInterceptor();
        var probe = new CommitWriteProbe(handler);
        context.AddService(disposer);
        context.AddService(probe);
        var person = new Person(context);
        probe.Writes.Clear();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        disposer.Transaction = transaction;

        // Act
        await Record.ExceptionAsync(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.True(disposer.Disposed);
        Assert.False(Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName)).Consumed);
    }

    [Fact]
    public async Task WhenInterceptorReplacesAppliedValueWithEqualInstance_ThenCommitWriteIsConsumed()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var probe = new CommitWriteProbe(handler);
        context.AddService(new CopyingWriteInterceptor());
        context.AddService(probe);
        var person = new Person(context);
        probe.Writes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.True(Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName)).Consumed);
    }

    /// <summary>
    /// Disposes <see cref="Transaction"/>, from a flow that does not carry it, on the first write that reaches the model.
    /// </summary>
    [RunsAfter(typeof(SubjectTransactionInterceptor))]
    [RunsBefore(typeof(CommitWriteProbe))]
    private sealed class DisposingWriteInterceptor : IWriteInterceptor
    {
        public SubjectTransaction? Transaction { get; set; }

        public bool Disposed { get; private set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (Transaction is { } transaction && !Disposed)
            {
                Disposed = true;
                RunOutsideTransactionFlow(transaction.Dispose);
            }

            next(ref context);
        }
    }

    /// <summary>
    /// Replaces every string written to the model with an equal, distinct instance.
    /// </summary>
    [RunsAfter(typeof(SubjectTransactionInterceptor))]
    [RunsBefore(typeof(CommitWriteProbe))]
    private sealed class CopyingWriteInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (context.NewValue is string text)
            {
                context.NewValue = (TProperty)(object)new string(text.AsSpan());
            }

            next(ref context);
        }
    }
}

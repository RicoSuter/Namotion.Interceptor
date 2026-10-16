using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class CommitWriteMarkerTests
{
    private static (IInterceptorSubjectContext Context, ITransactionHandler Handler, Person Person) CreateContext()
    {
        var handler = new NoOpHandler();
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        context.AddService<ITransactionHandler>(handler);
        return (context, handler, new Person(context));
    }

    [Fact]
    public async Task WhenPropertyValueAndHandlerMatch_ThenMarkIsConsumedOnce()
    {
        // Arrange
        var (context, handler, person) = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        using (CommitWriteMarker.Set(transaction, property, "Bob"))
        {
            // Act
            var first = CommitWriteMarker.TryConsume(handler, property, "Bob");
            var second = CommitWriteMarker.TryConsume(handler, property, "Bob");

            // Assert
            Assert.True(first);
            Assert.False(second);
        }
    }

    [Fact]
    public async Task WhenValueDiffers_ThenMarkDoesNotMatchButIsConsumed()
    {
        // Arrange
        var (context, handler, person) = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        using (CommitWriteMarker.Set(transaction, property, "Bob"))
        {
            // Act
            var changedValue = CommitWriteMarker.TryConsume(handler, property, "Changed");
            var checkedValue = CommitWriteMarker.TryConsume(handler, property, "Bob");

            // Assert
            Assert.False(changedValue);
            Assert.False(checkedValue);
        }
    }

    [Fact]
    public async Task WhenMarkedValueIsNull_ThenOnlyNullMatches()
    {
        // Arrange
        var (context, handler, person) = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        bool nonNullMatches;
        using (CommitWriteMarker.Set(transaction, property, null))
        {
            nonNullMatches = CommitWriteMarker.TryConsume(handler, property, "Bob");
        }

        bool nullMatches;
        using (CommitWriteMarker.Set(transaction, property, null))
        {
            nullMatches = CommitWriteMarker.TryConsume<string?>(handler, property, null);
        }

        // Assert
        Assert.False(nonNullMatches);
        Assert.True(nullMatches);
    }

    [Fact]
    public async Task WhenPropertyDiffers_ThenMarkDoesNotMatchAndStaysSet()
    {
        // Arrange
        var (context, handler, person) = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var markedProperty = new PropertyReference(person, nameof(Person.FirstName));
        var otherProperty = new PropertyReference(person, nameof(Person.LastName));

        using (CommitWriteMarker.Set(transaction, markedProperty, "Bob"))
        {
            // Act
            var otherMatches = CommitWriteMarker.TryConsume(handler, otherProperty, "Bob");
            var markedMatches = CommitWriteMarker.TryConsume(handler, markedProperty, "Bob");

            // Assert
            Assert.False(otherMatches);
            Assert.True(markedMatches);
        }
    }

    [Fact]
    public async Task WhenHandlerIsNotPartOfTransaction_ThenMarkDoesNotMatchAndStaysSet()
    {
        // Arrange
        var (context, handler, person) = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        using (CommitWriteMarker.Set(transaction, property, "Bob"))
        {
            // Act
            var foreignMatches = CommitWriteMarker.TryConsume(new NoOpHandler(), property, "Bob");
            var handlerMatches = CommitWriteMarker.TryConsume(handler, property, "Bob");

            // Assert
            Assert.False(foreignMatches);
            Assert.True(handlerMatches);
        }
    }

    [Fact]
    public async Task WhenReferenceValueIsOnlyEqual_ThenMarkMatchesOnlyTheSameInstance()
    {
        // Arrange
        var (context, handler, person) = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var checkedValue = new AlwaysEqual();

        // Act
        bool equalInstanceMatches;
        using (CommitWriteMarker.Set(transaction, property, checkedValue))
        {
            equalInstanceMatches = CommitWriteMarker.TryConsume(handler, property, new AlwaysEqual());
        }

        bool sameInstanceMatches;
        using (CommitWriteMarker.Set(transaction, property, checkedValue))
        {
            sameInstanceMatches = CommitWriteMarker.TryConsume(handler, property, checkedValue);
        }

        // Assert
        Assert.False(equalInstanceMatches);
        Assert.True(sameInstanceMatches);
    }

    [Fact]
    public async Task WhenValueIsNullableValueType_ThenEqualValueAndNullMatch()
    {
        // Arrange
        var (context, handler, person) = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        bool equalValueMatches;
        using (CommitWriteMarker.Set(transaction, property, (int?)5))
        {
            equalValueMatches = CommitWriteMarker.TryConsume<int?>(handler, property, 5);
        }

        bool otherValueMatches;
        using (CommitWriteMarker.Set(transaction, property, (int?)5))
        {
            otherValueMatches = CommitWriteMarker.TryConsume<int?>(handler, property, 6);
        }

        bool nullMatches;
        using (CommitWriteMarker.Set(transaction, property, null))
        {
            nullMatches = CommitWriteMarker.TryConsume<int?>(handler, property, null);
        }

        bool valueMatchesNullMark;
        using (CommitWriteMarker.Set(transaction, property, null))
        {
            valueMatchesNullMark = CommitWriteMarker.TryConsume<int?>(handler, property, 5);
        }

        // Assert
        Assert.True(equalValueMatches);
        Assert.False(otherValueMatches);
        Assert.True(nullMatches);
        Assert.False(valueMatchesNullMark);
    }

    [Fact]
    public async Task WhenScopeIsDisposedWithoutConsumption_ThenNothingMatches()
    {
        // Arrange
        var (context, handler, person) = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        using (CommitWriteMarker.Set(transaction, property, "Bob"))
        {
            // Leave the mark unconsumed so disposal is the only operation that can clear it.
        }

        // Act
        var matches = CommitWriteMarker.TryConsume(handler, property, "Bob");

        // Assert
        Assert.False(matches);
    }

    [Fact]
    public async Task WhenNestedScopeIsDisposed_ThenOuterMarkIsRestored()
    {
        // Arrange
        var (context, handler, person) = CreateContext();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var outerProperty = new PropertyReference(person, nameof(Person.FirstName));
        var innerProperty = new PropertyReference(person, nameof(Person.LastName));

        using (CommitWriteMarker.Set(transaction, outerProperty, "outer"))
        {
            using (CommitWriteMarker.Set(transaction, innerProperty, "inner"))
            {
                CommitWriteMarker.TryConsume(handler, innerProperty, "inner");
            }

            // Act
            var matches = CommitWriteMarker.TryConsume(handler, outerProperty, "outer");

            // Assert
            Assert.True(matches);
        }
    }

    private sealed class AlwaysEqual
    {
        public override bool Equals(object? obj) => obj is AlwaysEqual;

        public override int GetHashCode() => 0;
    }

    private sealed class NoOpHandler : ITransactionHandler
    {
        public void OnCapturing<TProperty>(SubjectTransaction transaction, PropertyReference property, TProperty newValue, ChangeOrigin origin)
        {
        }

        public void OnCommitting(SubjectTransaction transaction, ReadOnlySpan<SubjectPropertyChange> changes)
        {
        }
    }
}

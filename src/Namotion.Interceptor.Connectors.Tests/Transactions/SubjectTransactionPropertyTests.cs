using System.Reactive.Concurrency;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for property interception during transactions: write/read behavior, notifications, derived properties.
/// </summary>
public class SubjectTransactionPropertyTests : TransactionTestBase
{
    [Fact]
    public async Task WriteProperty_WhenTransactionActive_CapturesChange()
    {
        var context = CreateContext();
        var person = new Person(context);

        using var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort);
        person.FirstName = "John";

        Assert.Single(transaction.PendingChanges);
        var change = transaction.PendingChanges.Values.First();
        Assert.Equal("John", change.GetNewValue<string>());
        Assert.Null(change.GetOldValue<string>());
    }

    [Fact]
    public void WriteProperty_WhenNoTransaction_PassesThrough()
    {
        var context = CreateContext();
        var person = new Person(context);

        person.FirstName = "John";

        Assert.Equal("John", person.FirstName);
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public async Task WriteProperty_WhenIsCommittingTrue_PassesThrough()
    {
        var context = CreateContext();
        var person = new Person(context);
        string finalValue;

        using (var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort))
        {
            person.FirstName = "John";

            await transaction.CommitAsync(CancellationToken.None);

            finalValue = person.FirstName!;
        }

        Assert.Equal("John", finalValue);
    }

    [Fact]
    public async Task WriteProperty_DerivedPropertySkipped_NotCaptured()
    {
        var context = CreateContext();
        var person = new Person(context);

        using var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        _ = person.FullName;

        Assert.Equal(2, transaction.PendingChanges.Count);
        Assert.All(transaction.PendingChanges.Keys, key => Assert.False(key.Metadata.IsDerived));
    }

    [Fact]
    public async Task WriteProperty_SamePropertyMultipleTimes_LastWriteWins()
    {
        var context = CreateContext();
        var person = new Person(context);

        using var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort);
        person.FirstName = "John";
        person.FirstName = "Jane";
        person.FirstName = "Jack";

        Assert.Single(transaction.PendingChanges);
        var change = transaction.PendingChanges.Values.First();
        Assert.Equal("Jack", change.GetNewValue<string>());
        Assert.Null(change.GetOldValue<string>());
    }

    [Fact]
    public async Task WriteProperty_PreservesChangeContext_SourceAndTimestamps()
    {
        var context = CreateContext();
        var person = new Person(context);
        var mockSource = Mock.Of<ISubjectSource>();
        var changedTime = DateTime.UtcNow.AddMinutes(-5);
        var receivedTime = DateTime.UtcNow;

        using var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort);

        using (SubjectChangeContext.WithState(mockSource, changedTime, receivedTime))
        {
            person.FirstName = "John";
        }

        var change = transaction.PendingChanges.Values.First();
        Assert.Same(mockSource, change.Source);
        Assert.Equal(changedTime, change.ChangedTimestamp);
        Assert.Equal(receivedTime, change.ReceivedTimestamp);
    }

    [Fact]
    public async Task ReadProperty_WhenTransactionActive_ReturnsPendingValue()
    {
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Original";

        using (var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort))
        {
            person.FirstName = "Pending";
            var readValue = person.FirstName;

            Assert.Equal("Pending", readValue);
            Assert.Single(transaction.PendingChanges);
        }

        Assert.Equal("Original", person.FirstName);
    }

    [Fact]
    public void ReadProperty_WhenNoTransaction_ReturnsActualValue()
    {
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Stored";

        var result = person.FirstName;

        Assert.Equal("Stored", result);
    }

    [Fact]
    public async Task ReadProperty_WhenNoPendingChange_ReturnsActualValue()
    {
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Stored";

        using var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort);
        var result = person.FirstName;

        Assert.Equal("Stored", result);
        Assert.Empty(transaction.PendingChanges);
    }

    [Fact]
    public async Task ReadProperty_WhenIsCommittingTrue_ReturnsActualValue()
    {
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Original";
        string? readDuringCommit = null;

        var changeObservable = context.GetPropertyChangeObservable(Scheduler.Immediate);
        changeObservable.Subscribe(change =>
        {
            if (change.Property.Metadata.Name == nameof(Person.FirstName))
            {
                readDuringCommit = person.FirstName;
            }
        });

        using (var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort))
        {
            person.FirstName = "Pending";

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal("Pending", readDuringCommit);
        Assert.Equal("Pending", person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_FiresChangeNotifications()
    {
        var context = CreateContext();
        var person = new Person(context);
        var notifications = new List<SubjectPropertyChange>();

        var changeObservable = context.GetPropertyChangeObservable(Scheduler.Immediate);
        changeObservable.Subscribe(change => notifications.Add(change));

        using (var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort))
        {
            person.FirstName = "John";
            Assert.Empty(notifications);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.True(notifications.Count >= 1, "Expected at least 1 notification");
        Assert.Contains(notifications, n => n.Property.Metadata.Name == nameof(Person.FirstName));
        var firstNameNotification = notifications.First(n => n.Property.Metadata.Name == nameof(Person.FirstName));
        Assert.Equal("John", firstNameNotification.GetNewValue<string>());
    }

    [Fact]
    public async Task DisposeWithoutCommit_DoesNotFireChangeNotifications()
    {
        var context = CreateContext();
        var person = new Person(context);
        var notifications = new List<SubjectPropertyChange>();

        var changeObservable = context.GetPropertyChangeObservable(Scheduler.Immediate);
        changeObservable.Subscribe(change => notifications.Add(change));

        using (var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort))
        {
            person.FirstName = "John";
        }

        Assert.Empty(notifications);
    }

    [Fact]
    public async Task CommitAsync_WithMultipleSubjects_AppliesAllChanges()
    {
        var context = CreateContext();
        var person1 = new Person(context);
        var person2 = new Person(context);

        using (var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort))
        {
            person1.FirstName = "John";
            person2.FirstName = "Jane";

            Assert.Equal(2, transaction.PendingChanges.Count);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal("John", person1.FirstName);
        Assert.Equal("Jane", person2.FirstName);
    }

    [Fact]
    public async Task Integration_DerivedPropertyUpdates_AfterCommit()
    {
        var context = CreateContext();
        var person = new Person(context);

        using (var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            var duringTransaction = person.FullName;

            await transaction.CommitAsync(CancellationToken.None);

            var afterCommit = person.FullName;

            Assert.Equal("John Doe", duringTransaction);
            Assert.Equal("John Doe", afterCommit);
        }
    }
}

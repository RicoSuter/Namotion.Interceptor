using System.ComponentModel.DataAnnotations;
using System.Reactive.Concurrency;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for property interception during transactions: write/read behavior, notifications, derived properties.
/// </summary>
public class SubjectTransactionPropertyTests : TransactionTestBase
{
    [Fact]
    public async Task WriteProperty_WhenTransactionActive_CapturesChange()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";

        // Assert
        Assert.Single(transaction.PendingChanges);
        var change = transaction.PendingChanges.Values.First();
        Assert.Equal("John", change.GetNewValue<string>());
        Assert.Null(change.GetOldValue<string>());
    }

    [Fact]
    public void WriteProperty_WhenNoTransaction_PassesThrough()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public async Task WriteProperty_WhenIsCommittingTrue_PassesThrough()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        string finalValue;

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            await transaction.CommitAsync(CancellationToken.None);

            finalValue = person.FirstName!;
        }

        // Assert
        Assert.Equal("John", finalValue);
    }

    [Fact]
    public async Task WriteProperty_DerivedPropertySkipped_NotCaptured()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        _ = person.FullName;

        // Assert
        Assert.Equal(2, transaction.PendingChanges.Count);
        Assert.All(transaction.PendingChanges.Keys, key => Assert.False(key.Metadata.IsDerived));
    }

    [Fact]
    public async Task WriteProperty_SamePropertyMultipleTimes_LastWriteWins()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.FirstName = "Jane";
        person.FirstName = "Jack";

        // Assert
        Assert.Single(transaction.PendingChanges);
        var change = transaction.PendingChanges.Values.First();
        Assert.Equal("Jack", change.GetNewValue<string>());
        Assert.Null(change.GetOldValue<string>());
    }

    [Fact]
    public async Task WriteProperty_PreservesChangeContext_SourceAndTimestamps()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var mockSource = Mock.Of<ISubjectSource>();
        var changedTime = DateTime.UtcNow.AddMinutes(-5);
        var receivedTime = DateTime.UtcNow;

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        using (SubjectChangeContext.WithState(mockSource, changedTime, receivedTime))
        {
            person.FirstName = "John";
        }

        // Assert
        var change = transaction.PendingChanges.Values.First();
        Assert.Same(mockSource, change.Source);
        Assert.Equal(changedTime, change.ChangedTimestamp);
        Assert.Equal(receivedTime, change.ReceivedTimestamp);
    }

    [Fact]
    public async Task ReadProperty_WhenTransactionActive_ReturnsPendingValue()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Original";

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Pending";
            var readValue = person.FirstName;

            // Assert
            Assert.Equal("Pending", readValue);
            Assert.Single(transaction.PendingChanges);
        }

        Assert.Equal("Original", person.FirstName);
    }

    [Fact]
    public void ReadProperty_WhenNoTransaction_ReturnsActualValue()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Stored";

        // Act
        var result = person.FirstName;

        // Assert
        Assert.Equal("Stored", result);
    }

    [Fact]
    public async Task ReadProperty_WhenNoPendingChange_ReturnsActualValue()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Stored";

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var result = person.FirstName;

        // Assert
        Assert.Equal("Stored", result);
        Assert.Empty(transaction.PendingChanges);
    }

    [Fact]
    public async Task ReadProperty_WhenIsCommittingTrue_ReturnsActualValue()
    {
        // Arrange
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

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Pending";

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("Pending", readDuringCommit);
        Assert.Equal("Pending", person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_FiresChangeNotifications()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var notifications = new List<SubjectPropertyChange>();

        var changeObservable = context.GetPropertyChangeObservable(Scheduler.Immediate);
        changeObservable.Subscribe(change => notifications.Add(change));

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            Assert.Empty(notifications);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.True(notifications.Count >= 1, "Expected at least 1 notification");
        Assert.Contains(notifications, n => n.Property.Metadata.Name == nameof(Person.FirstName));
        var firstNameNotification = notifications.First(n => n.Property.Metadata.Name == nameof(Person.FirstName));
        Assert.Equal("John", firstNameNotification.GetNewValue<string>());
    }

    [Fact]
    public async Task DisposeWithoutCommit_DoesNotFireChangeNotifications()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var notifications = new List<SubjectPropertyChange>();

        var changeObservable = context.GetPropertyChangeObservable(Scheduler.Immediate);
        changeObservable.Subscribe(change => notifications.Add(change));

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
        }

        // Assert
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task CommitAsync_WithMultipleSubjects_AppliesAllChanges()
    {
        // Arrange
        var context = CreateContext();
        var person1 = new Person(context);
        var person2 = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person1.FirstName = "John";
            person2.FirstName = "Jane";

            Assert.Equal(2, transaction.PendingChanges.Count);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("John", person1.FirstName);
        Assert.Equal("Jane", person2.FirstName);
    }

    [Fact]
    public async Task Integration_DerivedPropertyUpdates_AfterCommit()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            var duringTransaction = person.FullName;

            await transaction.CommitAsync(CancellationToken.None);

            var afterCommit = person.FullName;

            // Assert
            Assert.Equal("John Doe", duringTransaction);
            Assert.Equal("John Doe", afterCommit);
        }
    }

    [Fact]
    public async Task WhenTransactionActive_ThenPropertyChangedNotFired()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            // Assert - PropertyChanged should NOT fire during transaction
            Assert.Empty(firedEvents);
        }
    }

    [Fact]
    public async Task WhenTransactionCommits_ThenPropertyChangedFired()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            Assert.Empty(firedEvents); // Not fired yet

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - PropertyChanged should fire after commit
        Assert.Contains("FirstName", firedEvents);
    }

    [Fact]
    public async Task WhenTransactionCommits_ThenDerivedPropertyChangedFired()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            Assert.Empty(firedEvents); // Not fired yet

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - PropertyChanged should fire for normal AND derived properties
        Assert.Contains("FirstName", firedEvents);
        Assert.Contains("LastName", firedEvents);
        Assert.Contains("FullName", firedEvents);
    }

    [Fact]
    public async Task CommitAsync_WithDependentProperties_CommitsInInsertionOrder()
    {
        // Arrange: Motor with MaxAllowedSpeed=100 and a validator that rejects MotorSpeed > MaxAllowedSpeed.
        // Setting MotorSpeed=150 requires MaxAllowedSpeed to be raised first.
        var context = CreateContextWithMotorValidation();
        var motor = new Motor(context);

        // Act: Raise limit first, then set speed above old limit
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        motor.MaxAllowedSpeed = 200;
        motor.MotorSpeed = 150;

        await transaction.CommitAsync(CancellationToken.None);

        // Assert: Both values applied because commit replayed in insertion order
        Assert.Equal(200, motor.MaxAllowedSpeed);
        Assert.Equal(150, motor.MotorSpeed);
    }

    [Fact]
    public async Task CommitAsync_WithDependentProperties_ValidatorRejectsInvalidSpeedDuringCapture()
    {
        // Arrange: Motor with MaxAllowedSpeed=100
        var context = CreateContextWithMotorValidation();
        var motor = new Motor(context);

        // Act & Assert: Setting MotorSpeed=150 without raising limit is rejected during capture
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        Assert.Throws<ValidationException>(() => motor.MotorSpeed = 150);
    }

    private static IInterceptorSubjectContext CreateContextWithMotorValidation()
    {
        return InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithService<IPropertyValidator>(() => new MotorSpeedValidator())
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking()
            .WithSourceTransactions();
    }

    /// <summary>
    /// Validates that MotorSpeed does not exceed MaxAllowedSpeed.
    /// Reads MaxAllowedSpeed through the property getter, which returns the pending value during transactions.
    /// </summary>
    private class MotorSpeedValidator : IPropertyValidator
    {
        public IEnumerable<ValidationResult> Validate<TProperty>(PropertyReference property, TProperty value)
        {
            if (property.Metadata.Name == nameof(Motor.MotorSpeed) &&
                value is int speed &&
                property.Subject is Motor motor)
            {
                // Reading MaxAllowedSpeed goes through the interceptor chain,
                // so during a transaction it returns the pending value
                var maxAllowedSpeed = motor.MaxAllowedSpeed;
                if (speed > maxAllowedSpeed)
                {
                    yield return new ValidationResult(
                        $"MotorSpeed {speed} exceeds MaxAllowedSpeed {maxAllowedSpeed}.");
                }
            }
        }
    }
}

using System.Reactive.Concurrency;
using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Sources.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

public class SubjectTransactionTests
{
    [Fact]
    public void BeginTransaction_CreatesActiveTransaction()
    {
        // Arrange
        CreateContext();

        // Act
        using var transaction = SubjectTransaction.BeginTransaction();

        // Assert
        Assert.NotNull(transaction);
        Assert.Same(transaction, SubjectTransaction.Current);
    }

    [Fact]
    public void Dispose_CleansUpWithoutCommit_ImplicitRollback()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        SubjectTransaction? capturedTransaction;
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            capturedTransaction = transaction;
            person.FirstName = "John";

            // Assert - transaction is active
            Assert.NotNull(SubjectTransaction.Current);
            Assert.Single(transaction.PendingChanges);
        }

        // Assert - after dispose
        Assert.Null(SubjectTransaction.Current);
        Assert.Empty(capturedTransaction.PendingChanges);
        Assert.Null(person.FirstName); // Value not applied (rollback)
    }

    [Fact]
    public void BeginTransaction_WhenNested_ThrowsInvalidOperationException()
    {
        // Arrange
        CreateContext();
        using var transaction1 = SubjectTransaction.BeginTransaction();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => SubjectTransaction.BeginTransaction());
        Assert.Contains("Nested transactions are not supported", exception.Message);
    }

    [Fact]
    public async Task CommitAsync_WithNoChanges_ReturnsImmediately()
    {
        // Arrange
        CreateContext();
        using var transaction = SubjectTransaction.BeginTransaction();

        // Act - commit with no changes
        await transaction.CommitAsync(CancellationToken.None);

        // Assert - no exception thrown, completes successfully
        Assert.Empty(transaction.PendingChanges);
    }

    [Fact]
    public async Task CommitAsync_AppliesChangesToModel()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        using var transaction = SubjectTransaction.BeginTransaction();
        person.FirstName = "John";

        // Assert - during transaction, backing field not updated yet
        Assert.Single(transaction.PendingChanges);

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert - after commit, value is applied to model
        Assert.NotNull(person.FirstName);
        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_CleansUpPendingChanges()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        SubjectTransaction capturedTransaction;

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            capturedTransaction = transaction;
            person.FirstName = "John";

            Assert.Single(transaction.PendingChanges);

            // Act
            await transaction.CommitAsync(CancellationToken.None);

            // Assert - PendingChanges cleared after commit
            Assert.Empty(transaction.PendingChanges);
        }

        // Assert - transaction completely disposed after using block
        Assert.Null(SubjectTransaction.Current);
        Assert.Empty(capturedTransaction.PendingChanges);
    }

    [Fact]
    public async Task CommitAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        CreateContext();
        var transaction = SubjectTransaction.BeginTransaction();
        transaction.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await transaction.CommitAsync(CancellationToken.None));
    }
    
    [Fact]
    public void WriteProperty_WhenTransactionActive_CapturesChange()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        using var transaction = SubjectTransaction.BeginTransaction();
        // Act
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

        // Act - no transaction active
        person.FirstName = "John";

        // Assert - value written directly
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

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";

            // Act - commit applies changes (IsCommitting = true)
            await transaction.CommitAsync(CancellationToken.None);

            finalValue = person.FirstName!;
        }

        // Assert - value was applied through normal write path
        Assert.Equal("John", finalValue);
    }

    [Fact]
    public void WriteProperty_DerivedPropertySkipped_NotCaptured()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        using var transaction = SubjectTransaction.BeginTransaction();
        // Act - write to regular properties
        person.FirstName = "John";
        person.LastName = "Doe";

        // Access derived property (triggers a recalculation attempt)
        _ = person.FullName;

        // Assert - only regular properties captured, not derived
        Assert.Equal(2, transaction.PendingChanges.Count);
        Assert.All(transaction.PendingChanges.Keys, key => Assert.False(key.Metadata.IsDerived));
    }

    [Fact]
    public void WriteProperty_SamePropertyMultipleTimes_LastWriteWins()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        using var transaction = SubjectTransaction.BeginTransaction();
        // Act - write same property multiple times
        person.FirstName = "John";
        person.FirstName = "Jane";
        person.FirstName = "Jack";

        // Assert - only one entry, last value wins
        Assert.Single(transaction.PendingChanges);
        var change = transaction.PendingChanges.Values.First();
        Assert.Equal("Jack", change.GetNewValue<string>());
        Assert.Null(change.GetOldValue<string>()); // Original old value preserved
    }

    [Fact]
    public void WriteProperty_PreservesChangeContext_SourceAndTimestamps()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var mockSource = Mock.Of<ISubjectSource>();
        var changedTime = DateTime.UtcNow.AddMinutes(-5);
        var receivedTime = DateTime.UtcNow;

        using var transaction = SubjectTransaction.BeginTransaction();
     
        // Act - write with change context
        using (SubjectChangeContext.WithState(mockSource, changedTime, receivedTime))
        {
            person.FirstName = "John";
        }

        // Assert - context preserved in pending change
        var change = transaction.PendingChanges.Values.First();
        Assert.Same(mockSource, change.Source);
        Assert.Equal(changedTime, change.ChangedTimestamp);
        Assert.Equal(receivedTime, change.ReceivedTimestamp);
    }


    [Fact]
    public void ReadProperty_WhenTransactionActive_ReturnsPendingValue()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Original"; // Set initial value

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            // Act - write new value in transaction
            person.FirstName = "Pending";
            var readValue = person.FirstName;

            // Assert - read returns pending value, not stored value
            Assert.Equal("Pending", readValue);
            Assert.Single(transaction.PendingChanges);
        }

        // Assert - after rollback, original value remains
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
    public void ReadProperty_WhenNoPendingChange_ReturnsActualValue()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Stored";

        using var transaction = SubjectTransaction.BeginTransaction();
        // Act - read without pending change
        var result = person.FirstName;

        // Assert - returns actual stored value
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

        // Subscribe to change events to capture value during commit
        var changeObservable = context.GetPropertyChangeObservable(Scheduler.Immediate);
        changeObservable.Subscribe(change =>
        {
            if (change.Property.Metadata.Name == nameof(Person.FirstName))
            {
                readDuringCommit = person.FirstName;
            }
        });

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "Pending";

            // Act
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - during commit, actual storage value is read (which has been updated)
        Assert.Equal("Pending", readDuringCommit);
        Assert.Equal("Pending", person.FirstName);
    }


    [Fact]
    public async Task CommitAsync_WithSourceBoundProperty_WritesToSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(sourceMock.Object);

        // Act
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        sourceMock.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithSourceWriteFailure_ThrowsAggregateException()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Write failed"));

        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(sourceMock.Object);

        // Act & Assert
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => transaction.CommitAsync(CancellationToken.None));

            Assert.Single(exception.InnerExceptions);
            Assert.IsType<SourceWriteException>(exception.InnerExceptions[0]);
        }

        // Value isn't applied to the model due to source failure
        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithMixedSourceAndLocal_AppliesLocalAndSuccessfulSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var failingSource = new Mock<ISubjectSource>();
        failingSource.Setup(s => s.WriteBatchSize).Returns(0);
        failingSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Write failed"));

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        firstNameProp.SetSource(failingSource.Object);
        // LastName has no source - should always apply

        // Act
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John"; // Will fail
            person.LastName = "Doe";   // No source, will succeed

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => transaction.CommitAsync(CancellationToken.None));

            Assert.Single(exception.InnerExceptions);
        }

        // Assert
        Assert.Null(person.FirstName); // Failed to write to source
        Assert.Equal("Doe", person.LastName); // No source, applied directly
    }

    [Fact]
    public async Task CommitAsync_WithMultipleSources_GroupsBySource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1Writes = new List<int>();
        var source1Mock = new Mock<ISubjectSource>();
        source1Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source1Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, _) => source1Writes.Add(changes.Length))
            .Returns(ValueTask.CompletedTask);

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, _) => source2Writes.Add(changes.Length))
            .Returns(ValueTask.CompletedTask);

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));
        firstNameProp.SetSource(source1Mock.Object);
        lastNameProp.SetSource(source2Mock.Object);

        // Act
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - each source called once with its changes
        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal(1, source1Writes[0]);
        Assert.Equal(1, source2Writes[0]);
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
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            Assert.Empty(notifications); // No notifications during capture

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - notifications fired after commit (includes derived property FullName)
        Assert.True(notifications.Count >= 1, "Expected at least 1 notification");
        Assert.Contains(notifications, n => n.Property.Metadata.Name == nameof(Person.FirstName));
        var firstNameNotification = notifications.First(n => n.Property.Metadata.Name == nameof(Person.FirstName));
        Assert.Equal("John", firstNameNotification.GetNewValue<string>());
    }

    [Fact]
    public void DisposeWithoutCommit_DoesNotFireChangeNotifications()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var notifications = new List<SubjectPropertyChange>();

        var changeObservable = context.GetPropertyChangeObservable(Scheduler.Immediate);
        changeObservable.Subscribe(change => notifications.Add(change));

        // Act
        using (SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            // Dispose without commit
        }

        // Assert - no notifications fired
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
        using (var transaction = SubjectTransaction.BeginTransaction())
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
    public async Task CommitAsync_WithMultipleContexts_ResolvesCallbacksPerContext()
    {
        // Arrange - create two separate contexts with different source configurations
        var context1 = CreateContext();
        var context2 = CreateContext();

        var person1 = new Person(context1);
        var person2 = new Person(context2);

        var source1Writes = new List<int>();
        var source1Mock = new Mock<ISubjectSource>();
        source1Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source1Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, _) => source1Writes.Add(changes.Length))
            .Returns(ValueTask.CompletedTask);

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, _) => source2Writes.Add(changes.Length))
            .Returns(ValueTask.CompletedTask);

        new PropertyReference(person1, nameof(Person.FirstName)).SetSource(source1Mock.Object);
        new PropertyReference(person2, nameof(Person.FirstName)).SetSource(source2Mock.Object);

        // Act - single transaction with subjects from different contexts
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person1.FirstName = "John";
            person2.FirstName = "Jane";

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - each context's source was called independently
        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal("John", person1.FirstName);
        Assert.Equal("Jane", person2.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WhenCalledTwice_ThrowsInvalidOperationException()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);

            // Act & Assert - second commit should throw
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transaction.CommitAsync(CancellationToken.None));

            Assert.Contains("already been committed", exception.Message);
        }
    }

    [Fact]
    public async Task CommitAsync_WhenCancelled_PropagatesCancellation()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - cancellation from source write is wrapped in SourceWriteException -> AggregateException
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => transaction.CommitAsync(cts.Token));

            // Verify cancellation was propagated (wrapped in SourceWriteException)
            var sourceWriteException = Assert.Single(exception.InnerExceptions.OfType<SourceWriteException>());
            Assert.IsType<OperationCanceledException>(sourceWriteException.InnerException);
        }
    }

    [Fact]
    public void SetSource_StoresSourceReference()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();

        // Act
        property.SetSource(sourceMock);

        // Assert
        Assert.True(property.TryGetSource(out var retrievedSource));
        Assert.Same(sourceMock, retrievedSource);
    }

    [Fact]
    public void TryGetSource_WhenNoSourceSet_ReturnsFalse()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = property.TryGetSource(out var source);

        // Assert
        Assert.False(result);
        Assert.Null(source);
    }

    [Fact]
    public void SetSource_WhenCalledMultipleTimes_ReplacesReference()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source1Mock = Mock.Of<ISubjectSource>();
        var source2Mock = Mock.Of<ISubjectSource>();

        // Act
        property.SetSource(source1Mock);
        property.SetSource(source2Mock);

        // Assert
        Assert.True(property.TryGetSource(out var retrievedSource));
        Assert.Same(source2Mock, retrievedSource);
        Assert.NotSame(source1Mock, retrievedSource);
    }

    [Fact]
    public void RemoveSource_ClearsSourceReference()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();
        property.SetSource(sourceMock);

        // Act
        property.RemoveSource();

        // Assert
        Assert.False(property.TryGetSource(out _));
    }


    [Fact]
    public async Task Integration_WithMockSource_VerifyWriteChangesAsyncCalled()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);

        var capturedChanges = new List<SubjectPropertyChange>();
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, _) =>
            {
                foreach (var change in changes.Span)
                {
                    capturedChanges.Add(change);
                }
            })
            .Returns(ValueTask.CompletedTask);

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));
        firstNameProp.SetSource(sourceMock.Object);
        lastNameProp.SetSource(sourceMock.Object);

        // Act
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(2, capturedChanges.Count);
        Assert.Contains(capturedChanges, c => c.Property.Metadata.Name == nameof(Person.FirstName) && c.GetNewValue<string>() == "John");
        Assert.Contains(capturedChanges, c => c.Property.Metadata.Name == nameof(Person.LastName) && c.GetNewValue<string>() == "Doe");

        // Verify source method was called
        sourceMock.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Integration_DerivedPropertyUpdates_AfterCommit()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            // During transaction, derived property shows pending values
            // because read interceptor returns pending values for dependencies
            var duringTransaction = person.FullName;

            await transaction.CommitAsync(CancellationToken.None);

            // After commit, derived property still shows same values (now committed)
            var afterCommit = person.FullName;

            // Assert - derived property shows pending values during transaction
            Assert.Equal("John Doe", duringTransaction);
            Assert.Equal("John Doe", afterCommit);
        }
    }


    [Fact]
    public void InterceptorRegistration_TransactionBeforeObservable()
    {
        // Arrange
        var context = CreateContext();

        // Act - check if interceptor is in the chain with correct ordering
        var writeInterceptors = context.GetServices<Interceptors.IWriteInterceptor>().ToList();
        var transactionInterceptor = writeInterceptors.OfType<SubjectTransactionInterceptor>().FirstOrDefault();

        // Assert
        Assert.NotNull(transactionInterceptor);

        // Transaction should be BEFORE PropertyChangeObservable to suppress notifications during capture
        var types = writeInterceptors.Select(i => i.GetType().Name).ToList();
        var transactionIndex = types.IndexOf("SubjectTransactionInterceptor");
        var observableIndex = types.IndexOf("PropertyChangeObservable");
        Assert.True(transactionIndex < observableIndex,
            $"Transaction interceptor should be before PropertyChangeObservable. Order: {string.Join(" -> ", types)}");
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_IsIdempotent()
    {
        // Arrange
        var transaction = SubjectTransaction.BeginTransaction();
        Assert.NotNull(SubjectTransaction.Current);

        // Act - dispose multiple times
        transaction.Dispose();
        Assert.Null(SubjectTransaction.Current);

        // Second dispose should be no-op
        transaction.Dispose();
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public async Task AsyncLocalBehavior_CurrentClearedAfterUsingBlock()
    {
        // This test verifies that AsyncLocal is properly cleared after async commit
        // (important for async/await execution context behavior)
        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            Assert.NotNull(SubjectTransaction.Current);

            await transaction.CommitAsync(CancellationToken.None);

            // Current might not be null here (inside using, after await)
            // That's OK - we just want it null AFTER the using block
        }

        // AFTER the using block (Dispose runs in original context)
        Assert.Null(SubjectTransaction.Current);
    }

    #region Transaction Mode Tests

    [Fact]
    public async Task BestEffortMode_AppliesSuccessfulChanges_WhenSomeSourcesFail()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var successSource = CreateSucceedingSource();
        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        // Act
        using var tx = SubjectTransaction.BeginTransaction(TransactionMode.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<AggregateException>(() => tx.CommitAsync());

        // Assert - successful change IS applied, failed change is NOT
        Assert.Equal("John", person.FirstName);
        Assert.Null(person.LastName);
        Assert.Single(ex.InnerExceptions);
    }

    [Fact]
    public async Task StrictMode_AppliesNoChanges_WhenAnySourceFails()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var successSource = CreateSucceedingSource();
        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        // Act
        using var tx = SubjectTransaction.BeginTransaction(TransactionMode.Strict);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<AggregateException>(() => tx.CommitAsync());

        // Assert - NO changes applied to in-process model (strict = all-or-nothing)
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        Assert.Contains("No changes have been applied", ex.Message);
    }

    [Fact]
    public async Task RollbackMode_RevertsSuccessfulSources_WhenAnySourceFails()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var writeCallCount = 0;
        var successSource = new Mock<ISubjectSource>();
        successSource.Setup(s => s.WriteBatchSize).Returns(0);
        successSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback(() => writeCallCount++)
            .Returns(ValueTask.CompletedTask);

        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        // Act
        using var tx = SubjectTransaction.BeginTransaction(TransactionMode.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<AggregateException>(() => tx.CommitAsync());

        // Assert - NO changes applied, and revert was attempted (2 calls: initial + revert)
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        Assert.Equal(2, writeCallCount); // Initial write + revert
        Assert.Contains("Rollback was attempted", ex.Message);
    }

    [Fact]
    public async Task StrictMode_AppliesAllChanges_WhenAllSourcesSucceed()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        // Act
        using var tx = SubjectTransaction.BeginTransaction(TransactionMode.Strict);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        // Assert - all changes applied when all succeed
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task RollbackMode_AppliesAllChanges_WhenAllSourcesSucceed()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        // Act
        using var tx = SubjectTransaction.BeginTransaction(TransactionMode.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        // Assert - all changes applied when all succeed
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task RollbackMode_ReportsRevertFailures_WhenRevertAlsoFails()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var successThenFailSource = new Mock<ISubjectSource>();
        successThenFailSource.Setup(s => s.WriteBatchSize).Returns(0);
        successThenFailSource.SetupSequence(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask) // Initial write succeeds
            .ThrowsAsync(new InvalidOperationException("Revert failed")); // Revert fails

        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successThenFailSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        // Act
        using var tx = SubjectTransaction.BeginTransaction(TransactionMode.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<AggregateException>(() => tx.CommitAsync());

        // Assert - both original failure and revert failure reported
        Assert.Equal(2, ex.InnerExceptions.Count);
    }

    #endregion

    #region Transaction Requirement Tests

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
        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<AggregateException>(() => tx.CommitAsync());

        // Assert - validation fails before any writes
        var innerEx = Assert.Single(ex.InnerExceptions);
        Assert.IsType<InvalidOperationException>(innerEx);
        Assert.Contains("2 sources", innerEx.Message);
        Assert.Contains("only 1 is allowed", innerEx.Message);

        // No changes applied
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);

        // Sources were never called
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

        var source = new Mock<ISubjectSource>();
        source.Setup(s => s.WriteBatchSize).Returns(1); // Only 1 change per batch
        source.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        // Act
        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe"; // 2 changes > batch size of 1

        var ex = await Assert.ThrowsAsync<AggregateException>(() => tx.CommitAsync());

        // Assert - validation fails before any writes
        var innerEx = Assert.Single(ex.InnerExceptions);
        Assert.IsType<InvalidOperationException>(innerEx);
        Assert.Contains("2 changes", innerEx.Message);
        Assert.Contains("WriteBatchSize is 1", innerEx.Message);

        // Source was never called
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

        var source = new Mock<ISubjectSource>();
        source.Setup(s => s.WriteBatchSize).Returns(10); // Enough for 2 changes
        source.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        // Act
        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        // Assert - changes applied successfully
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);

        // Source was called once with both changes
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
        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        // Assert - changes applied successfully (0 = no limit)
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task SingleWriteRequirement_AllowsChangesWithoutSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        // No sources set - all changes are "without source"

        // Act
        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        // Assert - changes applied directly (no source validation needed)
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
        // LastName has no source

        // Act
        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe"; // No source - always allowed

        await tx.CommitAsync();

        // Assert - both changes applied
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task NoneRequirement_AllowsMultipleSources()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        // Act
        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.None);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        // Assert - changes applied successfully with multiple sources
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task SingleWriteWithRollback_RevertsOnFailure_WithSingleSource()
    {
        // Arrange - OPC UA scenario: single source, rollback mode, single write requirement
        var context = CreateContext();
        var person = new Person(context);

        var source = new Mock<ISubjectSource>();
        source.Setup(s => s.WriteBatchSize).Returns(10);
        source.SetupSequence(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Write failed")); // Single batch fails

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source.Object);

        // Act
        using var tx = SubjectTransaction.BeginTransaction(
            TransactionMode.Rollback,
            TransactionRequirement.SingleWrite);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<AggregateException>(() => tx.CommitAsync());

        // Assert - no changes applied, single failure reported (no revert needed - nothing succeeded)
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        Assert.Single(ex.InnerExceptions);
    }

    [Fact]
    public async Task DefaultRequirement_IsNone()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        // Act - default BeginTransaction() should allow multiple sources
        using var tx = SubjectTransaction.BeginTransaction(); // No requirement specified
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync();

        // Assert - changes applied successfully (default is None)
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    #endregion

    #region Helper Methods

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking()
            .WithSourceTransactions();
    }

    private static Mock<ISubjectSource> CreateSucceedingSource()
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        return mock;
    }

    private static Mock<ISubjectSource> CreateFailingSource()
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Source write failed"));
        return mock;
    }

    #endregion
}

using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

public class SubjectTransactionAsyncTests
{
    [Fact]
    public async Task TransactionLock_Serializes()
    {
        // Direct test of lock behavior
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        // Manually add the lock
        context.TryAddService(
            () => new TransactionLock(),
            existing => existing != null);

        var lockService = context.TryGetService<TransactionLock>();
        Assert.NotNull(lockService);

        // Acquire first lock
        var lock1 = await lockService.AcquireAsync(CancellationToken.None);

        // Start second lock acquisition in background
        var lock2AcquiredTcs = new TaskCompletionSource<bool>();
        var lock2Task = Task.Run(async () =>
        {
            var lock2 = await lockService.AcquireAsync(CancellationToken.None);
            lock2AcquiredTcs.SetResult(true);
            return lock2;
        });

        // Wait a bit to let lock2 task start
        await Task.Delay(100);

        // lock2 should still be waiting
        Assert.False(lock2AcquiredTcs.Task.IsCompleted, "lock2 should be waiting");

        // Release lock1
        lock1.Dispose();

        // Now lock2 should complete
        var lock2Result = await lock2Task;
        Assert.True(await lock2AcquiredTcs.Task);
        lock2Result.Dispose();
    }

    [Fact]
    public async Task TransactionLock_ViaBeginAsync_Serializes()
    {
        // Test that BeginAsync uses the same lock instance
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        // First call to BeginAsync will create the lock
        await using var tx1 = await context.BeginTransactionAsync();

        // Get the lock that was created
        var lockService = context.TryGetService<TransactionLock>();
        Assert.NotNull(lockService);

        // Now try to acquire another lock in background
        // Use ExecutionContext.SuppressFlow to prevent AsyncLocal inheritance
        var lock2AcquiredTcs = new TaskCompletionSource<bool>();
        Task<SubjectTransaction> tx2Task;
        using (ExecutionContext.SuppressFlow())
        {
            tx2Task = Task.Run(async () =>
            {
                var tx = await context.BeginTransactionAsync();
                lock2AcquiredTcs.SetResult(true);
                return tx;
            });
        }

        // Wait a bit
        await Task.Delay(100);

        // tx2 should be waiting because tx1 has not been disposed
        Assert.False(lock2AcquiredTcs.Task.IsCompleted, "tx2 should be waiting for lock");

        // Dispose tx1
        await tx1.DisposeAsync();

        // Now tx2 should complete
        await using var tx2 = await tx2Task;
        Assert.True(await lock2AcquiredTcs.Task);
    }

    [Fact]
    public async Task BeginTransactionAsync_ReturnsTransaction()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        await using var tx = await context.BeginTransactionAsync();

        Assert.NotNull(tx);
        Assert.Same(context, tx.Context);
    }

    [Fact]
    public async Task BeginTransactionAsync_SerializesTransactionsPerContext()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        await using var tx1 = await context.BeginTransactionAsync();

        // Verify tx1's context matches
        Assert.Same(context, tx1.Context);
        Assert.NotNull(SubjectTransaction.Current); // Verify AsyncLocal is set

        // Start tx2 - it should wait because tx1 holds the lock
        // Use ExecutionContext.SuppressFlow to prevent AsyncLocal inheritance
        var tx2Started = false;
        var tx2Acquired = false;
        Task<SubjectTransaction> tx2Task;
        using (ExecutionContext.SuppressFlow())
        {
            tx2Task = Task.Run(async () =>
            {
                tx2Started = true;
                var tx = await context.BeginTransactionAsync();
                tx2Acquired = true;
                return tx;
            });
        }

        // Give tx2 time to start
        while (!tx2Started)
            await Task.Delay(1);

        // tx2 should be waiting for lock (not yet completed)
        await Task.Delay(100);
        Assert.False(tx2Acquired, "tx2 should not have acquired lock yet");
        Assert.False(tx2Task.IsCompleted, "tx2 should be waiting for lock held by tx1");

        // Dispose tx1 to release lock
        await tx1.DisposeAsync();

        // Now tx2 should complete
        await using var tx2 = await tx2Task;
        Assert.NotNull(tx2);
    }

    [Fact]
    public async Task WriteProperty_ToDifferentContext_ThrowsInvalidOperationException()
    {
        var context1 = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var context2 = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person1 = new Person(context1);
        var person2 = new Person(context2);

        await using var tx = await context1.BeginTransactionAsync();

        // Should work - same context
        person1.FirstName = "John";

        // Should throw - different context
        var ex = Assert.Throws<InvalidOperationException>(() => person2.FirstName = "Jane");
        Assert.Contains("different context", ex.Message);
    }

    [Fact]
    public async Task WriteProperty_ToSameContext_Succeeds()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person1 = new Person(context);
        var person2 = new Person(context);

        // Note: subject.Context returns InterceptorExecutor, not the original context
        // But they share the same TransactionLock via fallback context mechanism

        await using var tx = await context.BeginTransactionAsync();

        // Verify transaction context is the original context
        Assert.Same(context, tx.Context);

        // Should work - same TransactionLock is accessible from both
        person1.FirstName = "John";
        person2.FirstName = "Jane";

        var changes = tx.GetPendingChanges();
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task BeginTransactionAsync_StoresStartTimestamp()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var beforeStart = DateTimeOffset.UtcNow;
        await using var tx = await context.BeginTransactionAsync();
        var afterStart = DateTimeOffset.UtcNow;

        Assert.True(tx.StartTimestamp >= beforeStart);
        Assert.True(tx.StartTimestamp <= afterStart);
    }

    [Fact]
    public async Task BeginTransactionAsync_WithConflictBehavior_StoresBehavior()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.Ignore);

        Assert.Equal(TransactionConflictBehavior.Ignore, tx.ConflictBehavior);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesLock_AllowsNewTransaction()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var tx1 = await context.BeginTransactionAsync();
        await tx1.DisposeAsync();

        // Should be able to start new transaction immediately
        await using var tx2 = await context.BeginTransactionAsync();
        Assert.NotNull(tx2);
    }

    [Fact]
    public async Task WriteProperty_WithFailOnConflict_ThrowsWhenPropertyChangedAfterTransactionStart()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Start transaction first
        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // Simulate an external change happening AFTER the transaction started
        // This is what happens when another source (e.g., OPC UA, MQTT) updates the property
        // while a transaction is in progress
        var firstNameRef = person.GetPropertyReference(nameof(Person.FirstName));
        var externalChangeTimestamp = DateTimeOffset.UtcNow.AddMilliseconds(100);
        firstNameRef.SetPropertyData("LastChangedTimestamp", externalChangeTimestamp);

        // Verify the external timestamp is after the transaction start
        var timestamp = firstNameRef.GetLastChangedTimestamp();
        Assert.NotNull(timestamp);
        Assert.True(timestamp.Value > tx.StartTimestamp, "External change timestamp should be after transaction start");

        // This should throw because LastChangedTimestamp > StartTimestamp
        var ex = Assert.Throws<TransactionConflictException>(() => person.FirstName = "Conflict");
        Assert.Contains(nameof(Person.FirstName), ex.Message);
        Assert.Single(ex.ConflictingProperties);
    }

    [Fact]
    public async Task WriteProperty_WithIgnoreConflict_DoesNotThrowWhenPropertyChanged()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Start transaction with Ignore behavior
        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.Ignore);

        // Simulate an external change happening AFTER the transaction started
        var firstNameRef = person.GetPropertyReference(nameof(Person.FirstName));
        var externalChangeTimestamp = DateTimeOffset.UtcNow.AddMilliseconds(100);
        firstNameRef.SetPropertyData("LastChangedTimestamp", externalChangeTimestamp);

        // This should NOT throw even though LastChangedTimestamp > StartTimestamp
        // because ConflictBehavior is Ignore
        person.FirstName = "NewValue";

        var changes = tx.GetPendingChanges();
        Assert.Single(changes);
    }

    [Fact]
    public async Task WriteProperty_WithFailOnConflict_AllowsWriteWhenNoConflict()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Start transaction BEFORE any writes (no LastChangedTimestamp yet)
        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // This should succeed - property has no LastChangedTimestamp
        person.FirstName = "NewValue";

        var changes = tx.GetPendingChanges();
        Assert.Single(changes);
        Assert.Equal("NewValue", changes[0].GetNewValue<string>());
    }

    [Fact]
    public async Task ReadProperty_WithFailOnConflict_ThrowsWhenPropertyChangedAfterTransactionStart()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        person.FirstName = "Original";

        // Start transaction first
        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // Simulate an external change happening AFTER the transaction started
        var firstNameRef = person.GetPropertyReference(nameof(Person.FirstName));
        var externalChangeTimestamp = DateTimeOffset.UtcNow.AddMilliseconds(100);
        firstNameRef.SetPropertyData("LastChangedTimestamp", externalChangeTimestamp);

        // This should throw because LastChangedTimestamp > StartTimestamp
        var ex = Assert.Throws<TransactionConflictException>(() => _ = person.FirstName);
        Assert.Contains(nameof(Person.FirstName), ex.Message);
        Assert.Single(ex.ConflictingProperties);
    }

    [Fact]
    public async Task ReadProperty_WithIgnoreConflict_DoesNotThrowWhenPropertyChanged()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        person.FirstName = "Original";

        // Start transaction with Ignore behavior
        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.Ignore);

        // Simulate an external change happening AFTER the transaction started
        var firstNameRef = person.GetPropertyReference(nameof(Person.FirstName));
        var externalChangeTimestamp = DateTimeOffset.UtcNow.AddMilliseconds(100);
        firstNameRef.SetPropertyData("LastChangedTimestamp", externalChangeTimestamp);

        // This should NOT throw because ConflictBehavior is Ignore
        var value = person.FirstName;
        Assert.Equal("Original", value);
    }

    [Fact]
    public async Task ReadProperty_WithFailOnConflict_AllowsReadWhenNoConflict()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        person.FirstName = "Original";

        // Small delay to ensure transaction starts after the property was set
        await Task.Delay(10);

        // Start transaction - timestamp is before transaction start, so no conflict
        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // Verify timestamp is before transaction start (no external change simulation)
        var firstNameRef = person.GetPropertyReference(nameof(Person.FirstName));
        var timestamp = firstNameRef.GetLastChangedTimestamp();
        Assert.NotNull(timestamp);
        Assert.True(timestamp.Value < tx.StartTimestamp, "Timestamp should be before transaction start");

        // This should succeed - no conflict detected
        var value = person.FirstName;
        Assert.Equal("Original", value);
    }

    [Fact]
    public async Task CommitAsync_WithFailOnConflict_ThrowsWhenPropertyChangedDuringTransaction()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Start transaction with FailOnConflict
        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // Make a change within the transaction (no conflict yet - property has no timestamp)
        person.FirstName = "TransactionValue";

        // Verify change was captured
        var changes = tx.GetPendingChanges();
        Assert.Single(changes);

        // Simulate an external change happening AFTER the write but BEFORE commit
        var firstNameRef = person.GetPropertyReference(nameof(Person.FirstName));
        var externalChangeTimestamp = DateTimeOffset.UtcNow.AddMilliseconds(100);
        firstNameRef.SetPropertyData("LastChangedTimestamp", externalChangeTimestamp);

        // CommitAsync should throw because the property was modified externally
        var ex = await Assert.ThrowsAsync<TransactionConflictException>(() => tx.CommitAsync());
        Assert.Contains(nameof(Person.FirstName), ex.Message);
        Assert.Single(ex.ConflictingProperties);
    }

    [Fact]
    public async Task CommitAsync_WithIgnoreConflict_DoesNotThrowWhenPropertyChanged()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Start transaction with Ignore behavior
        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.Ignore);

        // Make a change within the transaction
        person.FirstName = "TransactionValue";

        // Simulate an external change happening AFTER the write but BEFORE commit
        var firstNameRef = person.GetPropertyReference(nameof(Person.FirstName));
        var externalChangeTimestamp = DateTimeOffset.UtcNow.AddMilliseconds(100);
        firstNameRef.SetPropertyData("LastChangedTimestamp", externalChangeTimestamp);

        // CommitAsync should NOT throw because ConflictBehavior is Ignore
        await tx.CommitAsync();

        // Verify the value was applied
        Assert.Equal("TransactionValue", person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithFailOnConflict_SucceedsWhenNoConflict()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        // Start transaction with FailOnConflict
        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // Make a change within the transaction (no conflict - property has no timestamp)
        person.FirstName = "TransactionValue";

        // CommitAsync should succeed - no external changes
        await tx.CommitAsync();

        // Verify the value was applied
        Assert.Equal("TransactionValue", person.FirstName);
    }
}

using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for optimistic locking: concurrent transactions that serialize at commit time.
/// </summary>
public class SubjectTransactionOptimisticLockingTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();
    }

    [Fact]
    public async Task OptimisticLocking_ConflictDetection_ThrowsWhenValueChangedExternally()
    {
        // Arrange
        // Optimistic transaction should detect conflicts when underlying value changes
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Original";

        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // Change within transaction captures OldValue = "Original"
        person.FirstName = "FromTx";

        // Simulate external change by running outside transaction's AsyncLocal context
        Task externalTask;
        var asyncFlowControl = ExecutionContext.SuppressFlow();
        try
        {
            externalTask = Task.Run(() => person.FirstName = "ExternalChange");
        }
        finally
        {
            asyncFlowControl.Undo();
        }
        await externalTask;

        // Act & Assert
        // CommitAsync should throw because current value != captured OldValue
        var ex = await Assert.ThrowsAsync<TransactionConflictException>(() => tx.CommitAsync(CancellationToken.None));
        Assert.Contains(nameof(Person.FirstName), ex.Message);
    }

    [Fact]
    public async Task OptimisticLocking_AllowsConcurrentTransactionStart()
    {
        // Arrange
        // Optimistic transactions should not block each other at start time
        var context = CreateContext();
        var startTimes = new List<DateTimeOffset>();
        var tasks = new List<Task>();

        // Act
        // Start 5 optimistic transactions concurrently in separate execution contexts
        for (int i = 0; i < 5; i++)
        {
            Task task;
            using (ExecutionContext.SuppressFlow())
            {
                task = Task.Run(async () =>
                {
                    var startTime = DateTimeOffset.UtcNow;
                    var tx = await context.BeginTransactionAsync(
                        TransactionFailureHandling.BestEffort,
                        TransactionLocking.Optimistic);
                    lock (startTimes)
                    {
                        startTimes.Add(startTime);
                    }
                    // Hold briefly then dispose
                    await Task.Delay(50);
                    tx.Dispose();
                });
            }
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        // Assert
        // All transactions should have started within a very short window
        var startRange = startTimes.Max() - startTimes.Min();
        Assert.True(startRange < TimeSpan.FromMilliseconds(200),
            $"Optimistic transactions should start concurrently, but took {startRange.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task ExclusiveLocking_BlocksOtherExclusiveTransactions()
    {
        // Arrange
        // Exclusive transactions should serialize (one at a time)
        var context = CreateContext();
        var person = new Person(context);

        var tx1Acquired = new TaskCompletionSource<bool>();
        var tx1Complete = new TaskCompletionSource<bool>();

        Task tx1Task;
        using (ExecutionContext.SuppressFlow())
        {
            tx1Task = Task.Run(async () =>
            {
                using var tx = await context.BeginTransactionAsync(
                    TransactionFailureHandling.BestEffort,
                    TransactionLocking.Exclusive);
                tx1Acquired.SetResult(true);

                person.FirstName = "Tx1";

                // Hold the lock for a bit
                await Task.Delay(500);

                await tx.CommitAsync(CancellationToken.None);
                tx1Complete.SetResult(true);
            });
        }

        // Wait for tx1 to acquire the lock
        await tx1Acquired.Task;

        Task<SubjectTransaction> tx2Task;
        using (ExecutionContext.SuppressFlow())
        {
            // Try to start tx2 - should block until tx1 completes
            tx2Task = Task.Run(async () =>
            {
                return await context.BeginTransactionAsync(
                    TransactionFailureHandling.BestEffort,
                    TransactionLocking.Exclusive);
            });
        }

        // Act
        // Wait a bit - tx2 should still be waiting
        await Task.Delay(100);
        Assert.False(tx2Task.IsCompleted, "tx2 should wait for tx1 to complete");

        // Wait for tx1 to complete
        await tx1Complete.Task;

        // Now tx2 should complete
        using var tx2 = await tx2Task;
        person.LastName = "Tx2";
        await tx2.CommitAsync(CancellationToken.None);

        await tx1Task;

        // Assert
        Assert.Equal("Tx1", person.FirstName);
        Assert.Equal("Tx2", person.LastName);
    }

    [Fact]
    public async Task OptimisticLocking_WithConflictBehaviorIgnore_DoesNotThrowOnConflict()
    {
        // Arrange
        // Optimistic transaction with Ignore conflict behavior should succeed even with conflicts
        var context = CreateContext();
        var person = new Person(context);
        person.FirstName = "Original";

        // Start optimistic transaction
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.Ignore);

        person.FirstName = "FromTx";

        // Simulate external change outside transaction
        Task externalTask;
        var asyncFlowControl = ExecutionContext.SuppressFlow();
        try
        {
            externalTask = Task.Run(() => person.FirstName = "ExternalChange");
        }
        finally
        {
            asyncFlowControl.Undo();
        }
        await externalTask;

        // Act
        // Should NOT throw because ConflictBehavior is Ignore
        await tx.CommitAsync(CancellationToken.None);

        // Assert
        // Transaction value should overwrite the external change
        Assert.Equal("FromTx", person.FirstName);
    }

    [Fact]
    public async Task OptimisticLocking_TransactionHasCorrectLockingValue()
    {
        // Arrange
        var context = CreateContext();

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic);

        // Assert
        Assert.Equal(TransactionLocking.Optimistic, tx.Locking);
    }

    [Fact]
    public async Task ExclusiveLocking_TransactionHasCorrectLockingValue()
    {
        // Arrange
        var context = CreateContext();

        // Act
        using var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Exclusive);

        // Assert
        Assert.Equal(TransactionLocking.Exclusive, tx.Locking);
    }

    [Fact]
    public async Task DefaultLocking_IsExclusive()
    {
        // Arrange
        // The default locking mode should be Exclusive
        var context = CreateContext();

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Assert
        Assert.Equal(TransactionLocking.Exclusive, tx.Locking);
    }

    [Fact]
    public async Task OptimisticLocking_DisposeCleansUpWithoutHoldingLock()
    {
        // Arrange
        // Optimistic transactions should be disposable even if never committed
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using (var tx = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic))
        {
            person.FirstName = "PendingValue";
            // Don't commit - just dispose
        }

        // Assert
        // Value should not have been applied
        Assert.Null(person.FirstName);

        // Should be able to start a new transaction immediately
        using var tx2 = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Exclusive);

        person.FirstName = "NewValue";
        await tx2.CommitAsync(CancellationToken.None);

        Assert.Equal("NewValue", person.FirstName);
    }

    [Fact]
    public async Task OptimisticLocking_MultipleOptimisticCanCoexistInDifferentContexts()
    {
        // Arrange
        // Multiple optimistic transactions in separate contexts should coexist without blocking
        var context = CreateContext();
        var transactionCount = 0;
        var tasks = new List<Task>();

        // Act
        // Start multiple optimistic transactions in different execution contexts
        for (int i = 0; i < 3; i++)
        {
            Task task;
            using (ExecutionContext.SuppressFlow())
            {
                task = Task.Run(async () =>
                {
                    var tx = await context.BeginTransactionAsync(
                        TransactionFailureHandling.BestEffort,
                        TransactionLocking.Optimistic);
                    Interlocked.Increment(ref transactionCount);
                    Assert.Equal(TransactionLocking.Optimistic, tx.Locking);
                    await Task.Delay(50);
                    tx.Dispose();
                });
            }
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        // Assert
        // All transactions should have been created
        Assert.Equal(3, transactionCount);
    }

    [Fact]
    public async Task OptimisticLocking_CommitSerializesWithExclusive()
    {
        // Arrange
        // When an optimistic transaction commits, it should wait for any exclusive transaction
        var context = CreateContext();
        var person = new Person(context);

        var exclusiveAcquired = new TaskCompletionSource<bool>();
        var exclusiveComplete = new TaskCompletionSource<bool>();
        var optimisticStarted = new TaskCompletionSource<bool>();
        var optimisticCommitStarted = new TaskCompletionSource<bool>();

        Task exclusiveTask;
        Task optimisticTask;

        // Act
        // Start exclusive transaction
        using (ExecutionContext.SuppressFlow())
        {
            exclusiveTask = Task.Run(async () =>
            {
                using var tx = await context.BeginTransactionAsync(
                    TransactionFailureHandling.BestEffort,
                    TransactionLocking.Exclusive);
                exclusiveAcquired.SetResult(true);

                person.FirstName = "Exclusive";

                // Wait for optimistic to signal it's trying to commit
                await optimisticCommitStarted.Task;
                await Task.Delay(100);

                await tx.CommitAsync(CancellationToken.None);
                exclusiveComplete.SetResult(true);
            });
        }

        // Wait for exclusive to acquire
        await exclusiveAcquired.Task;

        // Start optimistic transaction
        using (ExecutionContext.SuppressFlow())
        {
            optimisticTask = Task.Run(async () =>
            {
                var tx = await context.BeginTransactionAsync(
                    TransactionFailureHandling.BestEffort,
                    TransactionLocking.Optimistic);
                optimisticStarted.SetResult(true);

                person.LastName = "Optimistic";

                // Signal we're about to commit
                optimisticCommitStarted.SetResult(true);

                // This should wait for exclusive to complete
                await tx.CommitAsync(CancellationToken.None);
                tx.Dispose();
            });
        }

        // Wait for optimistic to start (should be immediate since optimistic doesn't block)
        await optimisticStarted.Task;

        await Task.WhenAll(exclusiveTask, optimisticTask);

        // Assert
        // Both commits should have succeeded
        Assert.Equal("Exclusive", person.FirstName);
        Assert.Equal("Optimistic", person.LastName);
    }

    [Fact]
    public async Task ExclusiveLocking_WaitsForOptimisticCommit()
    {
        // Arrange
        // An exclusive transaction should wait for an optimistic commit that's in progress
        var context = CreateContext();
        var person = new Person(context);

        var optimisticCommitStarted = new TaskCompletionSource<bool>();
        var exclusiveStarted = new TaskCompletionSource<bool>();

        Task optimisticTask;
        Task exclusiveTask;

        // Act
        // Start optimistic transaction
        using (ExecutionContext.SuppressFlow())
        {
            optimisticTask = Task.Run(async () =>
            {
                using var tx = await context.BeginTransactionAsync(
                    TransactionFailureHandling.BestEffort,
                    TransactionLocking.Optimistic);

                person.FirstName = "Optimistic";

                // Wait for exclusive to try to start
                await exclusiveStarted.Task;

                // Signal commit is starting
                optimisticCommitStarted.SetResult(true);

                // This commit acquires the lock
                await tx.CommitAsync(CancellationToken.None);
            });
        }

        // Start exclusive transaction
        using (ExecutionContext.SuppressFlow())
        {
            exclusiveTask = Task.Run(async () =>
            {
                exclusiveStarted.SetResult(true);

                // Wait for optimistic to start its commit
                await optimisticCommitStarted.Task;

                // This should wait for the optimistic commit lock
                using var tx = await context.BeginTransactionAsync(
                    TransactionFailureHandling.BestEffort,
                    TransactionLocking.Exclusive);

                person.LastName = "Exclusive";
                await tx.CommitAsync(CancellationToken.None);
            });
        }

        await Task.WhenAll(optimisticTask, exclusiveTask);

        // Assert
        Assert.Equal("Optimistic", person.FirstName);
        Assert.Equal("Exclusive", person.LastName);
    }
}

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
    public async Task WhenMultipleOptimisticTransactionsBegin_ThenNoneBlocks()
    {
        // Arrange
        var context = CreateContext();
        var allBegan = new CountdownEvent(5);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new List<Task>();

        // Act: each flow begins an optimistic transaction and holds it open until all five have begun.
        for (var i = 0; i < 5; i++)
        {
            Task task;
            using (ExecutionContext.SuppressFlow())
            {
                task = Task.Run(async () =>
                {
                    var transaction = await context.BeginTransactionAsync(
                        TransactionFailureHandling.BestEffort,
                        TransactionLocking.Optimistic);
                    allBegan.Signal();
                    await release.Task;
                    transaction.Dispose();
                });
            }
            tasks.Add(task);
        }

        // Assert: the countdown reaches zero only if every begin completed while the other four
        // transactions were still open; if optimistic begin took the per-context lock, it never would.
        Assert.True(allBegan.Wait(TimeSpan.FromSeconds(10)),
            "Optimistic transactions blocked each other at begin.");
        release.SetResult(true);
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task WhenExclusiveTransactionActive_ThenOtherExclusiveTransactionWaits()
    {
        // Arrange
        // Exclusive transactions should serialize (one at a time)
        var context = CreateContext();
        var person = new Person(context);

        var tx1Acquired = new TaskCompletionSource<bool>();
        var tx1CanCommit = new TaskCompletionSource<bool>();
        var tx2Started = new TaskCompletionSource<bool>();

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

                await tx1CanCommit.Task;

                await tx.CommitAsync(CancellationToken.None);
            });
        }

        // Wait for tx1 to acquire the lock
        await tx1Acquired.Task;

        // tx2 begins, writes, and commits within its own flow (begin/use/commit must be in the same flow).
        Task tx2Task;
        using (ExecutionContext.SuppressFlow())
        {
            tx2Task = Task.Run(async () =>
            {
                tx2Started.SetResult(true);
                using var tx = await context.BeginTransactionAsync(
                    TransactionFailureHandling.BestEffort,
                    TransactionLocking.Exclusive);
                person.LastName = "Tx2";
                await tx.CommitAsync(CancellationToken.None);
            });
        }

        // Act
        await tx2Started.Task;
        await Task.Yield();
        Assert.False(tx2Task.IsCompleted, "tx2 should wait for tx1 to complete");

        // Release tx1 to commit; tx2 then unblocks and commits.
        tx1CanCommit.SetResult(true);
        await tx1Task;
        await tx2Task;

        // Assert
        Assert.Equal("Tx1", person.FirstName);
        Assert.Equal("Tx2", person.LastName);
    }

    [Fact]
    public async Task WhenOptimisticTransactionBegun_ThenLockingIsOptimistic()
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
    public async Task WhenExclusiveTransactionBegun_ThenLockingIsExclusive()
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
    public async Task WhenLockingNotSpecified_ThenDefaultIsExclusive()
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
    public async Task WhenOptimisticTransactionDisposedWithoutCommit_ThenNothingIsAppliedAndNewTransactionCanBegin()
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
    public async Task WhenOptimisticCommitsWhileExclusiveActive_ThenCommitWaitsForExclusive()
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
    public async Task WhenOptimisticCommitInProgress_ThenExclusiveBeginWaits()
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

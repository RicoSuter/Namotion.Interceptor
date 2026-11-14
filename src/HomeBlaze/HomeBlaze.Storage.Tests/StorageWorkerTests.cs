using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhenItemIsRunning_ThenNextItemWaitsForIt()
    {
        // Arrange
        using var worker = new StorageWorker();
        var firstStarted = NewSignal();
        var firstRelease = NewSignal();
        var order = new List<string>();

        var first = worker.RunAsync(async _ =>
        {
            order.Add("first started");
            firstStarted.SetResult();
            await firstRelease.Task;
            order.Add("first finished");
        }, CancellationToken.None);

        var second = worker.RunAsync(_ =>
        {
            order.Add("second");
            return Task.CompletedTask;
        }, CancellationToken.None);

        await firstStarted.Task.WaitAsync(Timeout);

        // Act
        var secondWasWaiting = !second.IsCompleted;
        firstRelease.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);

        // Assert
        Assert.True(secondWasWaiting);
        Assert.Equal(["first started", "first finished", "second"], order);
    }

    [Fact]
    public async Task WhenItemThrows_ThenCallerGetsExceptionAndLaterItemsRun()
    {
        // Arrange
        using var worker = new StorageWorker();

        var laterItemWasRun = false;

        // Act
        var failing = worker.RunAsync(_ => throw new InvalidOperationException("failed"), CancellationToken.None);
        await worker.RunAsync(_ =>
        {
            laterItemWasRun = true;
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(Timeout);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        Assert.True(laterItemWasRun);
    }

    [Fact]
    public async Task WhenItemCallsWorkerAgain_ThenCallIsRefusedAndLaterItemsRun()
    {
        // Arrange
        using var worker = new StorageWorker();
        var wasRun = false;
        var laterItemWasRun = false;
        Exception? exception = null;

        // Act
        await worker.RunAsync(
            async token => exception = await Record.ExceptionAsync(() => worker.RunAsync(_ =>
            {
                wasRun = true;
                return Task.CompletedTask;
            }, token)),
            CancellationToken.None).WaitAsync(Timeout);
        await worker.RunAsync(_ =>
        {
            laterItemWasRun = true;
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(Timeout);

        // Assert
        Assert.Equal(StorageWorker.CallFromWithinItemMessage, Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.False(wasRun);
        Assert.True(laterItemWasRun);
    }

    [Fact]
    public async Task WhenFlowStartedByItemCallsWorkerWhileItemRuns_ThenCallIsRefused()
    {
        // Arrange
        using var worker = new StorageWorker();
        var wasRun = false;
        Task? forked = null;

        // Act: the item does not await the task it starts, which carries the flow of the item.
        await worker.RunAsync(async _ =>
        {
            forked = Task.Run(() => worker.RunAsync(_ =>
            {
                wasRun = true;
                return Task.CompletedTask;
            }, CancellationToken.None));

            await Record.ExceptionAsync(() => forked);
        }, CancellationToken.None).WaitAsync(Timeout);

        // Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => forked!);
        Assert.Equal(StorageWorker.CallFromWithinItemMessage, exception.Message);
        Assert.False(wasRun);
    }

    [Fact]
    public async Task WhenFlowStartedWithoutExecutionFlowCallsWorkerWhileItemRuns_ThenWorkIsQueuedAndRunsAfterTheItem()
    {
        // Arrange
        using var worker = new StorageWorker();
        var itemRelease = NewSignal();
        var queued = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        // Act
        var item = worker.RunAsync(async token =>
        {
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(() => queued.SetResult(worker.RunAsync(_ =>
                {
                    order.Add("started by the item");
                    return Task.CompletedTask;
                }, token)));
            }

            await itemRelease.Task;
            order.Add("item finished");
        }, CancellationToken.None);

        var startedByItem = await queued.Task.WaitAsync(Timeout);
        var wasWaitingForItem = !startedByItem.IsCompleted;
        itemRelease.SetResult();
        await Task.WhenAll(item, startedByItem).WaitAsync(Timeout);

        // Assert
        Assert.True(wasWaitingForItem);
        Assert.Equal(["item finished", "started by the item"], order);
    }

    [Fact]
    public async Task WhenFlowOfItemCallsWorkerOnceCallerHasTheOutcome_ThenWorkIsQueued()
    {
        // Arrange
        using var worker = new StorageWorker();
        ExecutionContext? flowOfItem = null;

        // Act: nothing lies between the outcome of the item and the call, so the item must count as finished by then.
        await worker.RunAsync(_ =>
        {
            flowOfItem = ExecutionContext.Capture();
            return Task.CompletedTask;
        }, CancellationToken.None);

        var wasRun = false;
        Task? call = null;
        ExecutionContext.Run(
            flowOfItem!,
            _ => call = worker.RunAsync(_ =>
            {
                wasRun = true;
                return Task.CompletedTask;
            }, CancellationToken.None),
            null);

        // Assert
        await call!.WaitAsync(Timeout);
        Assert.True(wasRun);
    }

    [Fact]
    public async Task WhenFlowOfFinishedItemCallsWorker_ThenWorkIsQueuedBehindRunningItem()
    {
        // Arrange
        using var worker = new StorageWorker();
        ExecutionContext? flowOfFinishedItem = null;
        await worker.RunAsync(_ =>
        {
            flowOfFinishedItem = ExecutionContext.Capture();
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(Timeout);

        var runningStarted = NewSignal();
        var runningRelease = NewSignal();
        var running = worker.RunAsync(async _ =>
        {
            runningStarted.SetResult();
            await runningRelease.Task;
        }, CancellationToken.None);
        await runningStarted.Task.WaitAsync(Timeout);

        // Act: a task started by the finished item still carries its flow and calls in while another item runs.
        var lateWorkRan = false;
        Task? lateWork = null;
        ExecutionContext.Run(flowOfFinishedItem!, _ =>
        {
            lateWork = worker.RunAsync(_ =>
            {
                lateWorkRan = true;
                return Task.CompletedTask;
            }, CancellationToken.None);
        }, null);

        var ranNextToRunningItem = lateWorkRan;
        runningRelease.SetResult();
        await Task.WhenAll(running, lateWork!).WaitAsync(Timeout);

        // Assert
        Assert.False(ranNextToRunningItem);
        Assert.True(lateWorkRan);
    }

    [Fact]
    public async Task WhenWorkerIsCreatedWithinItemOfAnotherWorker_ThenItsItemsAreNotPartOfThatItem()
    {
        // Arrange
        using var outer = new StorageWorker();
        var innerItemFinished = NewSignal();
        var outerRelease = NewSignal();
        var order = new List<string>();
        StorageWorker? inner = null;
        Task? callIntoOuter = null;

        var outerItem = outer.RunAsync(async token =>
        {
            inner = new StorageWorker();
            _ = inner.RunAsync(_ =>
            {
                // Refused if the item of the inner worker counted as part of the item of the outer one.
                callIntoOuter = outer.RunAsync(_ =>
                {
                    order.Add("call into outer");
                    return Task.CompletedTask;
                }, CancellationToken.None);
                innerItemFinished.SetResult();
                return Task.CompletedTask;
            }, token);

            await outerRelease.Task;
            order.Add("outer item finished");
        }, CancellationToken.None);

        await innerItemFinished.Task.WaitAsync(Timeout);

        // Act
        outerRelease.SetResult();
        await Task.WhenAll(outerItem, callIntoOuter!).WaitAsync(Timeout);
        inner!.Dispose();

        // Assert
        Assert.Equal(["outer item finished", "call into outer"], order);
    }

    [Fact]
    public async Task WhenItemOfOneWorkerCallsAnotherWorker_ThenWorkRunsAndItemCanAwaitIt()
    {
        // Arrange
        using var first = new StorageWorker();
        using var second = new StorageWorker();

        var wasRunBySecondWorker = false;

        // Act
        await first.RunAsync(
            token => second.RunAsync(_ =>
            {
                wasRunBySecondWorker = true;
                return Task.CompletedTask;
            }, token),
            CancellationToken.None).WaitAsync(Timeout);

        // Assert
        Assert.True(wasRunBySecondWorker);
    }

    [Fact]
    public async Task WhenCallIsCancelled_ThenCancellationCarriesTheCancelledToken()
    {
        // Arrange
        using var worker = new StorageWorker();
        using var callerCancellation = new CancellationTokenSource();
        using var workCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        workCancellation.Cancel();

        // Act
        var cancelledBeforeStart = worker.RunAsync(_ => Task.CompletedTask, callerCancellation.Token);
        var cancelledByWork = worker.RunAsync(
            _ => throw new OperationCanceledException(workCancellation.Token), CancellationToken.None);

        // Assert
        var beforeStart = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledBeforeStart.WaitAsync(Timeout));
        var byWork = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledByWork.WaitAsync(Timeout));
        Assert.Equal(callerCancellation.Token, beforeStart.CancellationToken);
        Assert.Equal(workCancellation.Token, byWork.CancellationToken);
    }

    [Fact]
    public async Task WhenWorkerIsDisposed_ThenRunningItemFinishesAndQueuedItemIsCancelled()
    {
        // Arrange
        var worker = new StorageWorker();
        var runningStarted = NewSignal();
        var runningRelease = NewSignal();
        var hasRunningFinished = false;
        var running = worker.RunAsync(async _ =>
        {
            runningStarted.SetResult();
            await runningRelease.Task;
            hasRunningFinished = true;
        }, CancellationToken.None);
        var queued = worker.RunAsync(_ => Task.CompletedTask, CancellationToken.None);
        await runningStarted.Task.WaitAsync(Timeout);

        // Act
        worker.Dispose();
        runningRelease.SetResult();
        var afterDispose = worker.RunAsync(_ => Task.CompletedTask, CancellationToken.None);

        // Assert
        await running.WaitAsync(Timeout);
        Assert.True(hasRunningFinished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => afterDispose.WaitAsync(Timeout));
    }

    [Fact]
    public async Task WhenWorkerIsStopped_ThenStopCompletesAfterRunningItemHasFinished()
    {
        // Arrange
        var worker = new StorageWorker();
        var runningStarted = NewSignal();
        var runningRelease = NewSignal();
        var running = worker.RunAsync(async _ =>
        {
            runningStarted.SetResult();
            await runningRelease.Task;
        }, CancellationToken.None);
        var queued = worker.RunAsync(_ => Task.CompletedTask, CancellationToken.None);
        await runningStarted.Task.WaitAsync(Timeout);

        // Act
        var stop = worker.StopAsync();
        var stopWasWaiting = !stop.IsCompleted;
        runningRelease.SetResult();
        await stop.WaitAsync(Timeout);

        // Assert
        Assert.True(stopWasWaiting);
        Assert.True(running.IsCompletedSuccessfully);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
    }

    [Fact]
    public async Task WhenWorkerIsStoppedFromWithinItsRunningItem_ThenStopDoesNotWaitForThatItem()
    {
        // Arrange
        var worker = new StorageWorker();

        // Act
        var item = worker.RunAsync(_ => worker.StopAsync(), CancellationToken.None);
        await item.WaitAsync(Timeout);
        var afterStop = worker.RunAsync(_ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.True(item.IsCompletedSuccessfully);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => afterStop.WaitAsync(Timeout));
    }

    [Fact]
    public async Task WhenCallerTokenIsCancelledBeforeItemStarts_ThenItemIsNotRun()
    {
        // Arrange
        using var worker = new StorageWorker();
        using var cancellation = new CancellationTokenSource();
        var runningStarted = NewSignal();
        var runningRelease = NewSignal();
        var running = worker.RunAsync(async _ =>
        {
            runningStarted.SetResult();
            await runningRelease.Task;
        }, CancellationToken.None);
        await runningStarted.Task.WaitAsync(Timeout);

        var wasRun = false;
        var queued = worker.RunAsync(_ =>
        {
            wasRun = true;
            return Task.CompletedTask;
        }, cancellation.Token);

        // Act
        cancellation.Cancel();
        runningRelease.SetResult();
        await running.WaitAsync(Timeout);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        Assert.False(wasRun);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

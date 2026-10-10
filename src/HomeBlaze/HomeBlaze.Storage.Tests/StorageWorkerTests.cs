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

        // Act
        var failing = worker.RunAsync(_ => throw new InvalidOperationException("failed"), CancellationToken.None);
        var result = await worker.RunAsync(_ => Task.FromResult(42), CancellationToken.None).WaitAsync(Timeout);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task WhenItemCallsWorkerAgain_ThenInnerWorkRunsInline()
    {
        // Arrange
        using var worker = new StorageWorker();

        // Act
        var result = await worker.RunAsync(
            async token => await worker.RunAsync(_ => Task.FromResult("inner"), token),
            CancellationToken.None).WaitAsync(Timeout);

        // Assert
        Assert.Equal("inner", result);
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

        var ranInline = lateWorkRan;
        runningRelease.SetResult();
        await Task.WhenAll(running, lateWork!).WaitAsync(Timeout);

        // Assert
        Assert.False(ranInline);
        Assert.True(lateWorkRan);
    }

    [Fact]
    public async Task WhenFlowStartedByItemIsStillInWorker_ThenNextItemWaitsForIt()
    {
        // Arrange
        using var worker = new StorageWorker();
        var forkedStarted = NewSignal();
        var forkedRelease = NewSignal();
        var order = new List<string>();
        Task? forked = null;

        var first = worker.RunAsync(_ =>
        {
            // The item does not await the flow it starts, it only stays until that flow is in the worker.
            forked = Task.Run(() => worker.RunAsync(async token =>
            {
                order.Add("forked started");
                forkedStarted.SetResult();
                await forkedRelease.Task;
                await worker.RunAsync(_ =>
                {
                    order.Add("forked nested");
                    return Task.CompletedTask;
                }, token);
                order.Add("forked finished");
            }, CancellationToken.None));
            return forkedStarted.Task;
        }, CancellationToken.None);

        var second = worker.RunAsync(_ =>
        {
            order.Add("second");
            return Task.CompletedTask;
        }, CancellationToken.None);

        await first.WaitAsync(Timeout);

        // Act
        forkedRelease.SetResult();
        await Task.WhenAll(forked!, second).WaitAsync(Timeout);

        // Assert
        Assert.Equal(["forked started", "forked nested", "forked finished", "second"], order);
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
        string? nestedResult = null;
        Task? callIntoOuter = null;

        var outerItem = outer.RunAsync(async token =>
        {
            inner = new StorageWorker();
            _ = inner.RunAsync(async innerToken =>
            {
                nestedResult = await inner.RunAsync(_ => Task.FromResult("nested"), innerToken);
                callIntoOuter = outer.RunAsync(_ =>
                {
                    order.Add("call into outer");
                    return Task.CompletedTask;
                }, innerToken);
                innerItemFinished.SetResult();
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
        Assert.Equal("nested", nestedResult);
        Assert.Equal(["outer item finished", "call into outer"], order);
    }

    [Fact]
    public async Task WhenInlineCallHasCancelledToken_ThenWorkIsNotRunAndTaskIsCancelled()
    {
        // Arrange
        using var worker = new StorageWorker();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var wasRun = false;

        // Act
        var inline = await worker.RunAsync<Task>(_ => Task.FromResult(worker.RunAsync(_ =>
        {
            wasRun = true;
            return Task.CompletedTask;
        }, cancellation.Token)), CancellationToken.None).WaitAsync(Timeout);

        // Assert
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inline.WaitAsync(Timeout));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(wasRun);
    }

    [Fact]
    public async Task WhenInlineWorkThrowsSynchronously_ThenTaskIsFaultedAndLaterItemsRun()
    {
        // Arrange
        using var worker = new StorageWorker();

        // Act
        var inline = await worker.RunAsync<Task>(
            _ => Task.FromResult<Task>(worker.RunAsync<int>(_ => throw new InvalidOperationException("failed"), CancellationToken.None)),
            CancellationToken.None).WaitAsync(Timeout);
        var result = await worker.RunAsync(_ => Task.FromResult(42), CancellationToken.None).WaitAsync(Timeout);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => inline);
        Assert.Equal(42, result);
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
        var running = worker.RunAsync(async _ =>
        {
            runningStarted.SetResult();
            await runningRelease.Task;
            return "finished";
        }, CancellationToken.None);
        var queued = worker.RunAsync(_ => Task.CompletedTask, CancellationToken.None);
        await runningStarted.Task.WaitAsync(Timeout);

        // Act
        worker.Dispose();
        runningRelease.SetResult();
        var afterDispose = worker.RunAsync(_ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.Equal("finished", await running.WaitAsync(Timeout));
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
            return "finished";
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
        Assert.Equal("finished", await running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
    }

    [Fact]
    public async Task WhenWorkerIsStoppedFromWithinItsRunningItem_ThenStopDoesNotWaitForThatItem()
    {
        // Arrange
        var worker = new StorageWorker();

        // Act
        var result = await worker.RunAsync(async _ =>
        {
            await worker.StopAsync();
            return "finished";
        }, CancellationToken.None).WaitAsync(Timeout);
        var afterStop = worker.RunAsync(_ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.Equal("finished", result);
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

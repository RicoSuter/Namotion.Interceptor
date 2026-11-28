using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Testing;
using Namotion.ZWave.Tests.Testing;

namespace Namotion.ZWave.Tests;

[CollectionDefinition(nameof(UnobservedTaskExceptionTests), DisableParallelization = true)]
public class UnobservedTaskExceptionCollection;

// Runs alone and collects the garbage of earlier tests before it listens, so only its own tasks can be reported.
[Collection(nameof(UnobservedTaskExceptionTests))]
public class UnobservedTaskExceptionTests
{
    private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromMilliseconds(1600);

    [Fact]
    public async Task WhenConnectFailsBecauseAWriteTimesOut_ThenNoTaskExceptionIsUnobserved()
    {
        // Act
        var unobserved = await CollectUnobservedExceptionsAsync(ConnectWithHungWriteAsync);

        // Assert
        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task WhenConnectFailsBecauseTheControllerStoppedResponding_ThenNoTaskExceptionIsUnobserved()
    {
        // Act
        var unobserved = await CollectUnobservedExceptionsAsync(ConnectToUnresponsiveControllerAsync);

        // Assert
        Assert.Empty(unobserved);
    }

    private static async Task<IReadOnlyCollection<Exception>> CollectUnobservedExceptionsAsync(Func<Task> scenario)
    {
        CollectGarbage();
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args) => unobserved.Enqueue(args.Exception);

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await scenario();
            CollectGarbage();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        return unobserved;
    }

    // Not inlined, so that no task of the scenario stays reachable from the caller's frame during the collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ConnectWithHungWriteAsync()
    {
        var time = new TimerRecordingTimeProvider();
        var stick = FakeZWaveStick.CreateWithController();
        stick.HangWrites = true;

        var connect = ZWaveController.ConnectAsync(stick, new ZWaveControllerOptions { TimeProvider = time });
        await time.WaitForTimerAsync(AcknowledgementTimeout);
        time.Advance(AcknowledgementTimeout);

        await Assert.ThrowsAsync<ZWaveConnectionException>(() => connect.WaitAsync(TestTimeouts.Default));
        Assert.True(stick.IsDisposed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ConnectToUnresponsiveControllerAsync()
    {
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };

        var connect = ZWaveController.ConnectAsync(stick, new ZWaveControllerOptions { TimeProvider = time });
        await time.WaitForTimerAsync(AcknowledgementTimeout);
        time.Advance(AcknowledgementTimeout);
        await time.WaitForTimerAsync(TimeSpan.FromMilliseconds(100));
        time.Advance(TimeSpan.FromMilliseconds(100));
        await time.WaitForTimerAsync(AcknowledgementTimeout, occurrence: 2);
        time.Advance(AcknowledgementTimeout);
        await time.WaitForTimerAsync(TimeSpan.FromMilliseconds(1100));
        time.Advance(TimeSpan.FromMilliseconds(1100));
        await time.WaitForTimerAsync(AcknowledgementTimeout, occurrence: 3);
        time.Advance(AcknowledgementTimeout);

        await Assert.ThrowsAsync<ZWaveConnectionException>(() => connect.WaitAsync(TestTimeouts.Default));
        Assert.True(stick.IsDisposed);
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

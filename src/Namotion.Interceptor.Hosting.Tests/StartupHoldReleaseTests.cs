using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// Covers the paths where an attached service's queued start never runs, which used to leak its
/// startup-completion hold.
/// </summary>
public class StartupHoldReleaseTests
{
    [Fact]
    public async Task WhenAServiceIsAttachedAfterTheHostStopped_ThenItsStartupHoldIsStillReleased()
    {
        // Arrange
        var deferrer = new CountingDeferrer();
        var (context, host) = await CreateStartedHostAsync(deferrer);
        await host.StopAsync();

        // Act - the queue is no longer drained, so this start will never run.
        var person = new Person(context);
        person.AttachHostedService(new PersonBackgroundService(person));

        // Assert - synchronous on this path, since StopAsync has already awaited the pump.
        Assert.Equal(0, deferrer.OutstandingHolds);
    }

    [Fact]
    public async Task WhenAStartIsStillQueuedAsTheHostStops_ThenItsStartupHoldIsStillReleased()
    {
        // Arrange
        // The case the tracking exists for. Starts run one at a time, so parking the first leaves the
        // second sitting in the queue; the pump then exits without ever dequeuing it, and its holds
        // live only in a closure nobody will run.
        var deferrer = new CountingDeferrer();
        var (context, host) = await CreateStartedHostAsync(deferrer);

        using var firstEntered = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var person = new Person(context);
        person.AttachHostedService(new GatedHostedService(firstEntered, releaseFirst));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));

        person.AttachHostedService(new PersonBackgroundService(person));
        Assert.Equal(2, deferrer.TakenCount);

        // Act - the queued second start is abandoned when the pump exits.
        var stopping = host.StopAsync();
        releaseFirst.Set();
        await stopping;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => deferrer.OutstandingHolds == 0);
    }

    [Fact]
    public async Task WhenAnAwaitedAttachIsAbandoned_ThenTheCallerIsUnblockedRatherThanLeftWaiting()
    {
        // Arrange
        var deferrer = new CountingDeferrer();
        var (context, host) = await CreateStartedHostAsync(deferrer);
        await host.StopAsync();

        // Act & Assert - the start will never run, so the awaiter has to be released rather than
        // left waiting on a completion nothing will ever set. Timed out rather than left to hang, so
        // a regression here fails CI instead of wedging it.
        var person = new Person(context);
        var attach = person.AttachHostedServiceAsync(new PersonBackgroundService(person), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => attach.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WhenAnAwaitedAttachIsStillQueuedAsTheHostStops_ThenTheCallerIsUnblocked()
    {
        // Arrange
        // The sweep's own completion path, which the test above does not reach: there the host had
        // already stopped, so the attach never reached the queue at all.
        var deferrer = new CountingDeferrer();
        var (context, host) = await CreateStartedHostAsync(deferrer);

        using var firstEntered = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var person = new Person(context);
        person.AttachHostedService(new GatedHostedService(firstEntered, releaseFirst));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));

        // Queued behind the parked start, so the pump exits without ever dequeuing it.
        var attach = person.AttachHostedServiceAsync(
            new PersonBackgroundService(person), CancellationToken.None);

        // Act
        var stopping = host.StopAsync();
        releaseFirst.Set();
        await stopping;

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => attach.WaitAsync(TimeSpan.FromSeconds(5)));
        await AsyncTestHelpers.WaitUntilAsync(() => deferrer.OutstandingHolds == 0);
    }

    [Fact]
    public async Task WhenTheHostIsDisposedWhileAStartIsRunning_ThenThatStartsHoldIsNotReleasedEarly()
    {
        // Arrange
        // Disposal must not open the completion gate for a start that is still executing, which is
        // the very thing the hold exists to prevent. Disposing does not wait for the pump, so the
        // sweep has to leave a running start's hold alone and let the start release it itself.
        var deferrer = new CountingDeferrer();
        var (context, host) = await CreateStartedHostAsync(deferrer);

        using var startEntered = new ManualResetEventSlim(false);
        using var releaseStart = new ManualResetEventSlim(false);
        var person = new Person(context);
        person.AttachHostedService(new GatedHostedService(startEntered, releaseStart));

        Assert.True(startEntered.Wait(TimeSpan.FromSeconds(5)));

        // Act
        host.Dispose();

        // Assert
        Assert.Equal(1, deferrer.OutstandingHolds);

        releaseStart.Set();
        await AsyncTestHelpers.WaitUntilAsync(() => deferrer.OutstandingHolds == 0);
    }

    [Fact]
    public async Task WhenAnAttachedServiceStartsNormally_ThenItsStartupHoldIsReleasedExactlyOnce()
    {
        // Arrange
        var deferrer = new CountingDeferrer();
        var (context, host) = await CreateStartedHostAsync(deferrer);

        // Act
        var person = new Person(context);
        await person.AttachHostedServiceAsync(new PersonBackgroundService(person), CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => deferrer.OutstandingHolds == 0);
        await host.StopAsync();
        Assert.Equal(1, deferrer.TakenCount);

        // The raw count, since the latch would hide the sweep releasing what the start released.
        Assert.Equal(1, deferrer.DisposeCallCount);
    }

    private static async Task<(IInterceptorSubjectContext Context, IHost Host)> CreateStartedHostAsync(
        CountingDeferrer deferrer)
    {
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithHostedServices(builder.Services);

        context.AddService(deferrer);

        var host = builder.Build();
        await host.StartAsync();
        return (context, host);
    }

    /// <summary>A service whose start parks until released, so a start can be caught mid-flight.</summary>
    private sealed class GatedHostedService(ManualResetEventSlim entered, ManualResetEventSlim release) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Stands in for the real deferrer, counting holds instead of gating anything.</summary>
    private sealed class CountingDeferrer : IStartupCompletionDeferrer
    {
        private int _taken;
        private int _released;
        private int _disposeCalls;

        public int TakenCount => Volatile.Read(ref _taken);

        public int OutstandingHolds => TakenCount - Volatile.Read(ref _released);

        /// <summary>Every Dispose call, latched or not, so a double release is visible.</summary>
        public int DisposeCallCount => Volatile.Read(ref _disposeCalls);

        public IDisposable DeferCompletion()
        {
            Interlocked.Increment(ref _taken);
            return new Hold(this);
        }

        private sealed class Hold(CountingDeferrer deferrer) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                Interlocked.Increment(ref deferrer._disposeCalls);

                // Latched like the real hold.
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Increment(ref deferrer._released);
                }
            }
        }
    }
}

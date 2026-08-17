using System;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Client;
using Namotion.Interceptor.WebSocket.Internal;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

/// <summary>
/// The client reports liveness from two places: the accepted handshake and the exit of the receive
/// loop that the handshake starts. Neither is observable without a real server.
/// </summary>
[Trait("Category", "Integration")]
public class WebSocketClientLivenessTests
{
    private readonly ITestOutputHelper _output;

    public WebSocketClientLivenessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheClientConnects_ThenItReportsOperationalUntilItStops()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational,
            message: "The client should report operational once the handshake is accepted.");

        // Assert
        Assert.NotNull(source.Diagnostics.OperationalChangeTime);
        Assert.NotNull(source.Diagnostics.StartTime);

        // Act
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(source.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenTheConnectionDrops_ThenLivenessFallsAndRisesAgainOnReconnect()
    {
        // Arrange - a reconnect delay far longer than the poll interval below, so the client cannot
        // be back before the drop has been observed.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port, reconnectDelay: TimeSpan.FromSeconds(3));

        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                message: "The client should report operational once the handshake is accepted.");

            var connectedAt = source.Diagnostics.OperationalChangeTime;

            // Act - Disconnect is the soft fault: it aborts the socket without stopping the connector,
            // so the monitor loop reconnects to the still-running server.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => !source.Diagnostics.IsOperational,
                message: "A client whose receive loop has exited should stop reporting that it is serving.");

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                message: "The client should report operational again once it has reconnected.");

            // The rise is a second transition rather than the first one never having been dropped.
            Assert.True(source.Diagnostics.OperationalChangeTime > connectedAt);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheClientIsKilled_ThenLivenessFallsAndRisesOnAReplacementAttempt()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port);
        await source.StartAsync(CancellationToken.None);

        var livenessFell = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var livenessRose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational);
            source.LivenessTestHooks = new WebSocketClientLivenessTestHooks
            {
                ReceiveLoopLivenessChanged = isOperational =>
                {
                    if (!isOperational)
                    {
                        livenessFell.TrySetResult();
                    }
                    else if (livenessFell.Task.IsCompleted)
                    {
                        livenessRose.TrySetResult();
                    }
                }
            };

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);
            await livenessFell.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await livenessRose.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(source.Diagnostics.IsOperational);
        }
        finally
        {
            source.LivenessTestHooks = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenOrdinaryReloadIsKilled_ThenForceKillStartsReplacementAttempt()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        var reloadGate = new ReconnectReloadGate();
        await using var source = CreateClientSource(portLease.Port, writeInterceptor: reloadGate);
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational);

        var ordinaryReconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOrdinaryReconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementReconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectAttempts = 0;
        source.LivenessTestHooks = new WebSocketClientLivenessTestHooks
        {
            BeforeReceiveLoopConnectionAttempt = () =>
            {
                var reconnectAttempt = Interlocked.Increment(ref reconnectAttempts);
                if (reconnectAttempt == 1)
                {
                    ordinaryReconnectStarted.TrySetResult();
                    allowOrdinaryReconnect.Task.GetAwaiter().GetResult();
                }
                else if (reconnectAttempt == 2)
                {
                    replacementReconnectStarted.TrySetResult();
                }
            }
        };
        reloadGate.Arm();

        try
        {
            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
            await ordinaryReconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            server.Root!.Name = "Reload";
            allowOrdinaryReconnect.TrySetResult();
            await reloadGate.ReloadStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);
            reloadGate.Release();

            // Assert
            await replacementReconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Volatile.Read(ref reconnectAttempts) >= 2);
        }
        finally
        {
            allowOrdinaryReconnect.TrySetResult();
            reloadGate.Release();
            source.LivenessTestHooks = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenPreviousReceiveLoopTimesOutBeforeUpdateAdmission_ThenLateUpdateCannotOverwriteReplacementWelcome()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        using var admissionGate = new UpdateAdmissionGate();
        await using var source = CreateClientSource(portLease.Port);
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational && clientRoot.Name == "Initial");

        var oldLoopCompletion = GetReceiveLoopCompletion(source);
        var previousLoopWaitBypassed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.LivenessTestHooks = new WebSocketClientLivenessTestHooks
        {
            BeforeUpdateCommitAdmission = admissionGate.Wait,
            WaitForPreviousReceiveLoopAsync = _ =>
            {
                previousLoopWaitBypassed.TrySetResult();
                return Task.CompletedTask;
            }
        };
        admissionGate.Arm();

        try
        {
            server.Root!.Name = "Old";
            await admissionGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            server.Root.Name = "Recovered";

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);
            await previousLoopWaitBypassed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientRoot.Name == "Recovered",
                message: "The replacement Welcome should load while the retired old loop is still paused.");
            admissionGate.Release();
            await oldLoopCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal("Recovered", clientRoot.Name);
        }
        finally
        {
            admissionGate.Release();
            source.LivenessTestHooks = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenOldReceiveLoopCommitIsAdmittedBeforeReplacement_ThenWelcomeCommitsLast()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        using var commitGate = new GatedWriteInterceptor("Old");
        await using var source = CreateClientSource(portLease.Port, writeInterceptor: commitGate);
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational && clientRoot.Name == "Initial");

        var previousLoopWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.LivenessTestHooks = new WebSocketClientLivenessTestHooks
        {
            WaitForPreviousReceiveLoopAsync = previousLoop =>
            {
                previousLoopWaitStarted.TrySetResult();
                return previousLoop;
            }
        };

        try
        {
            server.Root!.Name = "Old";
            await commitGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            server.Root.Name = "Recovered";
            await GetReceiveCancellation(source).CancelAsync();

            // Act
            var replacementTask = (Task<TimeSpan>)typeof(WebSocketSubjectClientSource)
                .GetMethod("ReconnectAndResumeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(source,
                [
                    "WebSocket reconnected during test",
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None
                ])!;
            var replacementAdvancedBeforeDrain = previousLoopWaitStarted.Task.IsCompleted;
            commitGate.Release();
            await replacementTask.WaitAsync(TimeSpan.FromSeconds(10));
            await AsyncTestHelpers.WaitUntilAsync(() => clientRoot.Name == "Recovered");

            // Assert
            Assert.False(replacementAdvancedBeforeDrain);
            Assert.Equal("Recovered", clientRoot.Name);
        }
        finally
        {
            commitGate.Release();
            source.LivenessTestHooks = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenShutdownTimesOutBeforeUpdateAdmission_ThenLateUpdateIsRejected()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        using var admissionGate = new UpdateAdmissionGate();
        await using var source = CreateClientSource(portLease.Port);
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational && clientRoot.Name == "Initial");

        var oldLoopCompletion = GetReceiveLoopCompletion(source);
        source.LivenessTestHooks = new WebSocketClientLivenessTestHooks
        {
            BeforeUpdateCommitAdmission = admissionGate.Wait,
            WaitForPreviousReceiveLoopAsync = static _ => Task.CompletedTask
        };
        admissionGate.Arm();
        server.Root!.Name = "Old";
        await admissionGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        Task? stopTask = null;
        try
        {
            // Act
            stopTask = source.StopAsync(CancellationToken.None);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            admissionGate.Release();
            await oldLoopCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal("Initial", clientRoot.Name);
        }
        finally
        {
            admissionGate.Release();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            source.LivenessTestHooks = null;
        }
    }

    [Fact]
    public async Task WhenShutdownRetiresAnAdmittedCommit_ThenStopWaitsForThatCommit()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        using var commitGate = new GatedWriteInterceptor("Old");
        await using var source = CreateClientSource(portLease.Port, writeInterceptor: commitGate);
        await source.StartAsync(CancellationToken.None);

        var clientRoot = (TestRoot)source.RootSubject;
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational && clientRoot.Name == "Initial");

        var previousLoopWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.LivenessTestHooks = new WebSocketClientLivenessTestHooks
        {
            WaitForPreviousReceiveLoopAsync = _ =>
            {
                previousLoopWaitStarted.TrySetResult();
                return Task.CompletedTask;
            }
        };
        server.Root!.Name = "Old";
        await commitGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await GetReceiveCancellation(source).CancelAsync();
        await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

        Task? cleanupTask = null;
        try
        {
            // Act
            cleanupTask = ((ValueTask)typeof(WebSocketSubjectClientSource)
                .GetMethod("DisposeWebSocketConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(source, null)!).AsTask();
            var shutdownAdvancedBeforeDrain = previousLoopWaitStarted.Task.IsCompleted;
            commitGate.Release();
            await cleanupTask.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.False(shutdownAdvancedBeforeDrain);
            Assert.Equal("Old", clientRoot.Name);
        }
        finally
        {
            commitGate.Release();
            if (cleanupTask is not null)
            {
                await cleanupTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            source.LivenessTestHooks = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenForceKillReplacementWaitsForWelcomeAndIsKilledAgain_ThenAnotherAttemptStarts()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WelcomeStallingServer();
        await server.StartAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port, reconnectDelay: TimeSpan.FromMilliseconds(20));
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational);

        var replacementAttempts = 0;
        var secondReplacementAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.LivenessTestHooks = new WebSocketClientLivenessTestHooks
        {
            BeforeReceiveLoopConnectionAttempt = () =>
            {
                if (Interlocked.Increment(ref replacementAttempts) == 2)
                {
                    secondReplacementAttemptStarted.TrySetResult();
                }
            }
        };

        try
        {
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);
            await server.ReplacementWaitingForWelcome.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert
            await secondReplacementAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(Volatile.Read(ref replacementAttempts) >= 2);
        }
        finally
        {
            source.LivenessTestHooks = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnOldReceiveLoopFinishes_ThenItCannotLowerReplacementLiveness()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port);
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational);

        var oldCompletion = GetReceiveLoopCompletion(source);
        var receiveCts = GetReceiveCancellation(source);
        var replacementCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetReceiveLoopCompletion(source, replacementCompletion);
        PublishReceiveLoopAndMarkOperational(source, replacementCompletion);

        try
        {
            // Act
            await receiveCts.CancelAsync();
            await oldCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(source.Diagnostics.IsOperational);
        }
        finally
        {
            replacementCompletion.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenAReplacementAttemptFailsAfterPreviousLoopTimeout_ThenMonitorAttemptsAnotherReconnect()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port);
        var monitorObservedInitialLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.LivenessTestHooks = new WebSocketClientLivenessTestHooks
        {
            MonitorReceiveLoopObserved = receiveLoop =>
            {
                if (receiveLoop is not null)
                {
                    monitorObservedInitialLoop.TrySetResult();
                }
            }
        };

        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational);
        await monitorObservedInitialLoop.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var staleCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiveCts = GetReceiveCancellation(source);
        var reconnectAttempts = 0;
        var secondReconnectAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.LivenessTestHooks = new WebSocketClientLivenessTestHooks
        {
            BeforeReceiveLoopConnectionAttempt = () =>
            {
                if (Interlocked.Increment(ref reconnectAttempts) == 2)
                {
                    secondReconnectAttempted.TrySetResult();
                }

                throw new InvalidOperationException("The test forces this replacement attempt to fail.");
            },
            WaitForPreviousReceiveLoopAsync = static _ => Task.CompletedTask
        };
        SetReceiveLoopCompletion(source, staleCompletion);

        try
        {
            // Act
            await receiveCts.CancelAsync();

            // Assert
            await secondReconnectAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            staleCompletion.TrySetResult();
            source.LivenessTestHooks = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnOldReceiveLoopIsPreemptedBeforeLoweringLiveness_ThenItCannotOverrideTheReplacement()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port);
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.IsOperational);

        var oldCompletion = GetReceiveLoopCompletion(source);
        var receiveCts = GetReceiveCancellation(source);
        var replacementCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldLoopReachedLivenessTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOldLoopToContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementPublicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.LivenessTestHooks = new WebSocketClientLivenessTestHooks
        {
            BeforeReceiveLoopLivenessTransition = () =>
            {
                oldLoopReachedLivenessTransition.TrySetResult();
                allowOldLoopToContinue.Task.GetAwaiter().GetResult();
            },
            BeforeReceiveLoopPublication = () => replacementPublicationStarted.TrySetResult()
        };

        try
        {
            // Act
            var cancellationTask = receiveCts.CancelAsync();
            await oldLoopReachedLivenessTransition.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacementPublicationTask = Task.Run(() =>
            {
                SetReceiveLoopCompletion(source, replacementCompletion);
                PublishReceiveLoopAndMarkOperational(source, replacementCompletion);
            });
            await replacementPublicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            allowOldLoopToContinue.TrySetResult();
            await cancellationTask;
            await replacementPublicationTask;
            await oldCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.True(source.Diagnostics.IsOperational);
        }
        finally
        {
            allowOldLoopToContinue.TrySetResult();
            source.LivenessTestHooks = null;
            replacementCompletion.TrySetResult();
        }
    }

    private async Task<WebSocketTestServer<TestRoot>> StartServerAsync(int port)
    {
        var server = new WebSocketTestServer<TestRoot>(_output);
        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: port);
        return server;
    }

    private static WebSocketSubjectClientSource CreateClientSource(
        int port,
        TimeSpan? reconnectDelay = null,
        IWriteInterceptor? writeInterceptor = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        if (writeInterceptor is not null)
        {
            context.AddService(writeInterceptor);
        }

        return new WebSocketSubjectClientSource(
            new TestRoot(context),
            new WebSocketClientConfiguration
            {
                ServerUri = new Uri($"ws://localhost:{port}/ws"),
                ReconnectDelay = reconnectDelay ?? TimeSpan.FromMilliseconds(200),
                MaxReconnectDelay = TimeSpan.FromSeconds(10)
            },
            NullLogger<WebSocketSubjectClientSource>.Instance);
    }

    private sealed class ReconnectReloadGate : IWriteInterceptor
    {
        private readonly TaskCompletionSource _reloadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowReload =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _blocked;

        public Task ReloadStarted => _reloadStarted.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _allowReload.TrySetResult();

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (Volatile.Read(ref _armed) == 1 &&
                context.Property.Name == nameof(TestRoot.Name) &&
                context.NewValue is "Reload" &&
                Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
            {
                _reloadStarted.TrySetResult();
                _allowReload.Task.GetAwaiter().GetResult();
            }

            next(ref context);
        }
    }

    private sealed class UpdateAdmissionGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _blocked;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Wait()
        {
            if (Volatile.Read(ref _armed) == 1 &&
                Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
            {
                _entered.TrySetResult();
                _release.Wait();
            }
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class GatedWriteInterceptor(string blockedValue) : IWriteInterceptor, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public Task Entered => _entered.Task;

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (Equals(context.NewValue, blockedValue) &&
                Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
            {
                _entered.TrySetResult();
                _release.Wait();
            }

            next(ref context);
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class WelcomeStallingServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource _replacementWaitingForWelcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebApplication? _application;
        private int _connectionCount;

        public Task ReplacementWaitingForWelcome => _replacementWaitingForWelcome.Task;

        public async Task StartAsync(int port)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

            var application = builder.Build();
            application.UseWebSockets();
            application.Map("/ws", HandleConnectionAsync);
            await application.StartAsync();
            _application = application;
        }

        private async Task HandleConnectionAsync(Microsoft.AspNetCore.Http.HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var connectionNumber = Interlocked.Increment(ref _connectionCount);

            try
            {
                using var hello = await WebSocketMessageReader.ReadMessageAsync(
                    webSocket,
                    64 * 1024,
                    _stopping.Token);
                if (!hello.Success)
                {
                    return;
                }

                if (connectionNumber == 1)
                {
                    var welcome = new WelcomePayload { Sequence = 0 };
                    var bytes = JsonWebSocketSerializer.Instance.SerializeMessage(MessageType.Welcome, welcome);
                    await webSocket.SendAsync(
                        bytes,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        _stopping.Token);
                }
                else if (connectionNumber == 2)
                {
                    _replacementWaitingForWelcome.TrySetResult();
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            if (_application is not null)
            {
                await _application.StopAsync();
                await _application.DisposeAsync();
            }
            _stopping.Dispose();
        }
    }

    private static CancellationTokenSource GetReceiveCancellation(WebSocketSubjectClientSource source) =>
        (CancellationTokenSource)typeof(WebSocketSubjectClientSource)
            .GetField("_receiveCts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source)!;

    private static TaskCompletionSource GetReceiveLoopCompletion(WebSocketSubjectClientSource source) =>
        (TaskCompletionSource)typeof(WebSocketSubjectClientSource)
            .GetField("_receiveLoopCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source)!;

    private static void SetReceiveLoopCompletion(
        WebSocketSubjectClientSource source,
        TaskCompletionSource completion) =>
        typeof(WebSocketSubjectClientSource)
            .GetField("_receiveLoopCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(source, completion);

    private static void PublishReceiveLoopAndMarkOperational(
        WebSocketSubjectClientSource source,
        TaskCompletionSource completion) =>
        typeof(WebSocketSubjectClientSource)
            .GetMethod("PublishReceiveLoopAndMarkOperational", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(source, [completion]);
}

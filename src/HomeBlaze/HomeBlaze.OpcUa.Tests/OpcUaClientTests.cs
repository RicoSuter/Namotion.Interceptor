using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.OpcUa.Tests;

public class OpcUaClientTests
{
    private const string NotRunningMessage = "Not running in a host, so nothing was started";
    private const string FactoryFailureMessage = "The factory could not build a client source.";

    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A diagnostics poll fast enough to observe. The production interval of ten seconds puts the poll
    /// out of reach of a suite that runs in seconds.
    /// </summary>
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20);

    /// <summary>A diagnostics value no poll produces, so the poll replacing it is visible.</summary>
    private const int SentinelItemCount = -1;

    [Fact]
    public async Task WhenTheClientIsStartedStoppedAndRestarted_ThenTheConnectorAndTheRootFollowIt()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();

        // Act
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForRunningClientAsync(client);

        // Assert
        var firstSource = Assert.IsAssignableFrom<ISubjectSource>(client.Connector);
        var firstRoot = client.Root;
        Assert.NotNull(firstRoot);
        Assert.NotNull(client.PendingWriteCount);
        Assert.NotNull(client.TotalReconnections);
        Assert.Null(client.StatusMessage);
        Assert.True(client.Stop_IsEnabled);

        // Act
        await client.StopAsync();

        // Assert
        Assert.Equal(ServiceStatus.Stopped, client.Status);
        Assert.Null(client.StatusMessage);
        Assert.False(client.IsEnabled);
        Assert.Null(client.Connector);
        Assert.Equal(SourceState.Stopped, firstSource.State);
        Assert.Null(client.Root);
        Assert.Null(client.PendingWriteCount);
        Assert.Null(client.TotalReconnections);
        Assert.True(client.Start_IsEnabled);

        // Act
        await client.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Running, client.Status);
        Assert.True(client.IsEnabled);
        Assert.NotNull(client.Connector);
        Assert.NotSame(firstSource, client.Connector);
        Assert.NotNull(client.Root);
        Assert.NotSame(firstRoot, client.Root);
    }

    [Fact]
    public async Task WhenAnEnabledClientEntersTheGraph_ThenItsConnectorIsStartedBeforeStartupCompletes()
    {
        // Arrange
        var recorder = new StartupCompletionRecorder();
        await using var testHost = await OpcUaTestHost.StartAsync(
            context => context.AddService<IStartupCompletion>(recorder));
        var client = testHost.CreateClient();
        recorder.Probe = () => client.Connector is not null;

        // Act
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForRunningClientAsync(client);
        await AsyncTestHelpers.WaitUntilAsync(
            () => recorder.Outstanding == 0,
            message: "A completion deferral was never released.");

        // Assert
        // The client's own completion deferral covers its start, which starts the source. Settling
        // without a source is a startup completion wait passing before the source has started.
        Assert.Equal(1, recorder.DeferralCount);
        Assert.True(recorder.ProbeWhenFirstSettled);
    }

    [Fact]
    public async Task WhenTheSubjectLeavesTheGraph_ThenTheConnectorIsStoppedAndDisposed()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync(context => context.WithSourceMonitoring());
        var monitor = testHost.Context.GetSourceMonitor();

        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForRunningClientAsync(client);

        var source = Assert.IsAssignableFrom<ISubjectSource>(client.Connector);
        Assert.Contains(source, monitor.Sources);

        // Act
        testHost.Container.Client = null;

        // Assert
        // A source unregisters from the monitor in Dispose and nowhere else, so this is the one
        // externally visible difference between the wrapper having stopped the source and having
        // disposed it.
        await AsyncTestHelpers.WaitUntilAsync(
            () => !monitor.Sources.Contains(source) && client.Status == ServiceStatus.Stopped,
            message: "The wrapper did not dispose the source, or did not report the stop.");

        Assert.Equal(SourceState.Stopped, source.State);
        Assert.Null(client.Connector);
        Assert.Null(client.Root);
        Assert.Null(client.TotalReconnections);
    }

    [Fact]
    public async Task WhenTheSubjectReEntersTheGraph_ThenANewConnectorStarts()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForRunningClientAsync(client);

        var firstSource = Assert.IsAssignableFrom<ISubjectSource>(client.Connector);
        var firstRoot = client.Root;

        testHost.Container.Client = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Status == ServiceStatus.Stopped && client.Connector is null,
            message: "Leaving the graph did not stop the client and its source.");

        // Act
        testHost.Container.Client = client;

        // Assert
        await OpcUaTestHost.WaitForRunningClientAsync(client);
        Assert.NotNull(client.Connector);
        Assert.NotSame(firstSource, client.Connector);
        Assert.Equal(SourceState.Stopped, firstSource.State);
        Assert.NotNull(client.Root);
        Assert.NotSame(firstRoot, client.Root);
    }

    [Fact]
    public async Task WhenAStartFailsAfterPublishingItsTree_ThenTheTreeAndTheDiagnosticsAreCleared()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(isEnabled: false);
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForWrapperStartedAsync(client);

        // Put in by hand: nothing running leaves them null on every other path into a start, and what
        // this pins is the failed start clearing them in its own right.
        client.IsConnected = true;
        client.IncomingChangesPerSecond = 1;
        client.OutgoingChangesPerSecond = 2;
        client.MonitoredItemCount = 3;
        client.PollingItemCount = 4;
        client.PendingWriteCount = 5;
        client.TotalReconnections = 6;

        ArmFactoryFailureAfterTheTreeIsPublished(testHost, client);

        // Act
        await client.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, client.Status);
        Assert.Equal(FactoryFailureMessage, client.StatusMessage);
        Assert.Null(client.Connector);
        Assert.Null(client.Root);
        Assert.Null(client.IsConnected);
        Assert.Null(client.IncomingChangesPerSecond);
        Assert.Null(client.OutgoingChangesPerSecond);
        Assert.Null(client.MonitoredItemCount);
        Assert.Null(client.PollingItemCount);
        Assert.Null(client.PendingWriteCount);
        Assert.Null(client.TotalReconnections);
    }

    [Fact]
    public async Task WhenTheWrappersOwnStartFails_ThenTheStartOperationStartsTheClient()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();

        // Armed before the client enters the graph, so the start that fails is the one the enabled client
        // issues from its own start.
        ArmFactoryFailureAfterTheTreeIsPublished(testHost, client);
        testHost.Container.Client = client;
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Status == ServiceStatus.Error && client.StatusMessage == FactoryFailureMessage,
            message: "The client's own start did not fail.");

        // Act
        // The one operation the UI offers at Error, since Stop_IsEnabled is false there.
        await client.StartAsync();

        // Assert
        // A failed start keeps no source, so the next start builds one rather than finding a failed one.
        Assert.Equal(ServiceStatus.Running, client.Status);
        Assert.Null(client.StatusMessage);
        Assert.NotNull(client.Connector);
        Assert.NotNull(client.Root);
    }

    [Fact]
    public async Task WhenAnUnconfiguredClientIsDisabled_ThenItReportsStopped()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(serverUrl: null);
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Error);
        Assert.Null(client.Connector);

        // Act
        client.IsEnabled = false;
        await client.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // The guarded start is skipped now that the client is disabled, so this stop is the last thing
        // that writes the status. Whatever it leaves behind is what the client reports from here on.
        Assert.Equal(ServiceStatus.Stopped, client.Status);
        Assert.Null(client.StatusMessage);
    }

    [Fact]
    public async Task WhenAnUnconfiguredClientIsDisabled_ThenTheErrorTextIsGoneBeforeTheStoppedStatus()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(serverUrl: null);
        testHost.Container.Client = client;
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Status == ServiceStatus.Error && client.StatusMessage is not null);

        // Each of the two writes is its own pass of the interceptor chain, so the window between them is
        // wide and observable. Read it from the status write, because nothing here can hold it open to
        // read afterwards.
        var messageBesideStopped = default(string);
        var reachedStopped = 0;
        testHost.WriteSeam.ArmAfterWrite((property, value) =>
        {
            if (ReferenceEquals(property.Subject, client) &&
                property.Name == nameof(OpcUaClient.Status) &&
                value is ServiceStatus.Stopped &&
                Interlocked.Increment(ref reachedStopped) == 1)
            {
                messageBesideStopped = client.StatusMessage;
            }
        });

        // Act
        client.IsEnabled = false;
        await client.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // The text stands under Error alone, so the status that leaves Error may not be written while it
        // is still there.
        Assert.Equal(1, reachedStopped);
        Assert.Null(messageBesideStopped);
    }

    [Fact]
    public async Task WhenAFaultedWrapperIsStartedAgain_ThenTheErrorTextIsGoneBeforeTheStartingStatus()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(serverUrl: null);
        testHost.Container.Client = client;
        // Compound, because a move to Error writes the status ahead of the message, so waiting on the
        // status alone can return before the text this test needs is there.
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Status == ServiceStatus.Error && client.StatusMessage is not null);

        var messageBesideStarting = default(string);
        var reachedStarting = 0;
        testHost.WriteSeam.ArmAfterWrite((property, value) =>
        {
            if (ReferenceEquals(property.Subject, client) &&
                property.Name == nameof(OpcUaClient.Status) &&
                value is ServiceStatus.Starting &&
                Interlocked.Increment(ref reachedStarting) == 1)
            {
                messageBesideStarting = client.StatusMessage;
            }
        });

        // Act
        // The one operation the UI offers at Error, and the other status that leaves it.
        await client.StartAsync();

        // Assert
        Assert.Equal(1, reachedStarting);
        Assert.Null(messageBesideStarting);
    }

    [Fact]
    public async Task WhenTheServerUrlIsNotConfigured_ThenTheStartFailsWithoutAConnector()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(serverUrl: null, isEnabled: false);
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForWrapperStartedAsync(client);

        // Act
        await client.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, client.Status);
        Assert.Equal("Server URL is not configured", client.StatusMessage);
        Assert.Null(client.Connector);
        Assert.Null(client.Root);
    }

    [Fact]
    public async Task WhenTheHostHasDrained_ThenTheStartReportsThatNothingWasStarted()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(isEnabled: false);
        testHost.Container.Client = client;
        await testHost.StopHostAsync();

        // Act
        await client.StartAsync();

        // Assert
        // The wrapper's run has ended, so a source started now would outlive it. The wrapper has to say
        // so rather than report a start that never comes.
        Assert.Equal(ServiceStatus.Error, client.Status);
        Assert.Equal(NotRunningMessage, client.StatusMessage);
        Assert.Null(client.Connector);
        Assert.Null(client.Root);
    }

    [Fact]
    public async Task WhenTheContextHasNoHostingHandler_ThenTheStartReportsThatNothingWasStarted()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var container = new WrapperContainer(context);
        var client = new OpcUaClient(NullLogger<OpcUaClient>.Instance)
        {
            ServerUrl = OpcUaTestHost.DeadServerUrl,
            IsEnabled = false
        };

        container.Client = client;

        // Act
        await client.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, client.Status);
        Assert.Equal(NotRunningMessage, client.StatusMessage);
        Assert.Null(client.Connector);
    }

    [Fact]
    public async Task WhenStartsAndStopsRunConcurrently_ThenTheWrapperNeverRunsMoreThanOneConnector()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync(context => context.WithSourceMonitoring());
        var monitor = testHost.Context.GetSourceMonitor();

        var client = testHost.CreateClient(isEnabled: false);
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForWrapperStartedAsync(client);

        using var operationsDone = new CancellationTokenSource();
        var mostSourcesSeen = 0;

        // Act
        // A source registers with the monitor in its start and unregisters in its dispose, so the
        // monitor counts every source that has been started and not yet disposed.
        var poll = Task.Run(async () =>
        {
            while (!operationsDone.IsCancellationRequested)
            {
                var count = monitor.Sources.Length;
                if (count > mostSourcesSeen)
                {
                    Interlocked.Exchange(ref mostSourcesSeen, count);
                }

                await Task.Yield();
            }
        });

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var round = 0; round < 3; round++)
            {
                await client.StartAsync();
                await client.StopAsync();
            }
        })));

        await operationsDone.CancelAsync();
        await poll;

        // Assert
        // Two live sources is what the gate exists to prevent: each start builds its own root, so the
        // second source would be unreachable from the wrapper and could never be stopped from here.
        // An upper bound, not an equality: the sampler is unsynchronized, every worker ends on a stop,
        // and on a small runner it can be starved across the whole burst without that meaning anything.
        // That every stopped source was disposed is settled below, deterministically.
        Assert.True(mostSourcesSeen <= 1, $"Saw {mostSourcesSeen} live sources at once.");

        // The gate serializes the paths but nothing orders the callers, so the state the race settles on
        // is whichever ran last. What has to hold is that one more start still gets a running client
        // rather than a wedged one, and that it is the only source left.
        await client.StartAsync();
        Assert.Equal(ServiceStatus.Running, client.Status);
        Assert.Same(client.Connector, Assert.Single(monitor.Sources));
        Assert.NotNull(client.Root);
    }

    [Fact]
    public async Task WhenAStartHoldsTheGate_ThenTheStartAndThePollBothStillFinish()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient(diagnosticsPollInterval: FastPoll);

        // The factory's first act is publishing the tree it is about to bind a source to, and the start
        // holds the gate across the whole start, so holding it there holds the gate.
        //
        // Armed before the client enters the graph, and the start left to the one the enabled client
        // issues from its own start rather than invoked here: the Start operation would be a second
        // start, which would queue on the gate behind the first one.
        using var factoryReached = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        var held = 0;
        testHost.WriteSeam.ArmAfterWrite((property, value) =>
        {
            if (!ReferenceEquals(property.Subject, client) ||
                property.Name != nameof(OpcUaClient.Root) ||
                value is null ||
                Interlocked.Exchange(ref held, 1) == 1)
            {
                return;
            }

            factoryReached.Set();
            releaseFactory.Wait(HoldTimeout);
        });

        try
        {
            // Act
            testHost.Container.Client = client;
            Assert.True(factoryReached.Wait(HoldTimeout), "The start never reached the factory.");

            // Nothing is observed while the gate is held. What this pins is liveness: a start parked in
            // its factory and a poll running against it both still finish.
        }
        finally
        {
            releaseFactory.Set();
        }

        // Assert
        // The diagnostics as well as the status, because the start writes Running before it publishes
        // them: the sentinel below has to go in after that write, or the start replacing it would stand
        // in for the poll that is what this waits for.
        await OpcUaTestHost.WaitForRunningClientAsync(client);
        Assert.NotNull(client.Connector);
        Assert.NotNull(client.Root);

        client.PollingItemCount = SentinelItemCount;
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.PollingItemCount != SentinelItemCount,
            message: "The diagnostics poll did not resume once the start released the gate.");
    }

    [Fact]
    public async Task WhenAnEnabledClientIsReconfigured_ThenItIsStartedAgainFromTheEdit()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Running);

        var firstSource = Assert.IsAssignableFrom<ISubjectSource>(client.Connector);
        var firstRoot = client.Root;

        // Act
        client.RootPath = "Machines/Edited";
        await client.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // The factory names the tree after the last segment of the configured path, so a tree with the
        // edited name is the start having run again against the edit rather than the first source still
        // being the one that runs.
        Assert.Equal(ServiceStatus.Running, client.Status);
        Assert.NotNull(client.Connector);
        Assert.NotSame(firstSource, client.Connector);
        Assert.Equal(SourceState.Stopped, firstSource.State);
        Assert.NotSame(firstRoot, client.Root);
        Assert.Equal("Edited", Assert.IsType<OpcUaDynamicSubject>(client.Root).Title);
    }

    [Fact]
    public async Task WhenADisabledClientIsReconfigured_ThenTheStopIsNotFollowedByAStart()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var client = testHost.CreateClient();
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Running);

        // Act
        client.IsEnabled = false;
        await client.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // Without the guard on the start, an edit that disables the client would stop it and start it
        // again in the same call.
        Assert.Equal(ServiceStatus.Stopped, client.Status);
        Assert.Null(client.Connector);
        Assert.Null(client.Root);
    }

    [Fact]
    public async Task WhenTheSubjectLeavesTheGraphWhileAStartIsInItsFactory_ThenNoConnectorOutlivesTheStop()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync(context => context.WithSourceMonitoring());
        var monitor = testHost.Context.GetSourceMonitor();

        // Disabled, so the wrapper starts nothing itself and the start below comes from another caller.
        var client = testHost.CreateClient(isEnabled: false);
        testHost.Container.Client = client;
        await OpcUaTestHost.WaitForWrapperStartedAsync(client);

        // Holding the factory keeps the start inside the gate while the wrapper's own stop begins, and
        // the factory then returns a source the stop can no longer have seen.
        using var rootWritten = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        var held = 0;
        testHost.WriteSeam.ArmAfterWrite((property, value) =>
        {
            if (!ReferenceEquals(property.Subject, client) ||
                property.Name != nameof(OpcUaClient.Root) ||
                value is null ||
                Interlocked.Exchange(ref held, 1) == 1)
            {
                return;
            }

            rootWritten.Set();
            releaseFactory.Wait(HoldTimeout);
        });

        Task start;
        try
        {
            start = client.StartAsync();
            Assert.True(rootWritten.Wait(HoldTimeout), "The start never reached the factory.");

            // Act
            testHost.Container.Client = null;

            // The run loop ends only once the wrapper's stop has canceled the run, so the source the
            // factory returns below is built after the stop began.
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.ExecuteTask is { IsCompleted: true },
                message: "The wrapper's stop did not begin.");
        }
        finally
        {
            releaseFactory.Set();
        }

        await start.WaitAsync(HoldTimeout);
        await OpcUaTestHost.WaitForStatusAsync(() => client.Status, ServiceStatus.Stopped);

        // Assert
        Assert.Null(client.StatusMessage);
        Assert.Null(client.Connector);
        Assert.Null(client.Root);
        Assert.Empty(monitor.Sources);
    }

    /// <summary>
    /// Fails the client's factory once, after it has published the tree it is about to bind a source to.
    /// The factory is private and builds from properties none of which can be made to fail, so the
    /// failure is injected into the write it starts with.
    /// </summary>
    private static void ArmFactoryFailureAfterTheTreeIsPublished(OpcUaTestHost testHost, OpcUaClient client)
    {
        var failed = 0;
        testHost.WriteSeam.ArmAfterWrite((property, value) =>
        {
            // Only the factory's own write, which publishes a tree. The wrapper clears Root from the same
            // property once nothing runs, and failing that write would fail the reporting rather than the
            // factory.
            if (!ReferenceEquals(property.Subject, client) ||
                property.Name != nameof(OpcUaClient.Root) ||
                value is null ||
                Interlocked.Exchange(ref failed, 1) == 1)
            {
                return;
            }

            testHost.WriteSeam.ArmAfterWrite(null);
            throw new InvalidOperationException(FactoryFailureMessage);
        });
    }
}

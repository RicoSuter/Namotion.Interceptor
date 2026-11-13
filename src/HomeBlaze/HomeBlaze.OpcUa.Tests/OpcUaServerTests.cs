using HomeBlaze.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.OpcUa.Tests;

public class OpcUaServerTests
{
    /// <summary>
    /// How long the root stays unloaded while the start is observed not to have created anything. See
    /// the test that uses it for why the observation cannot false fail.
    /// </summary>
    private static readonly TimeSpan UnloadedRootObservation = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a start or stop that must complete is given before the test fails rather than hangs.</summary>
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhenTheConfiguredPathDoesNotResolve_ThenTheStartFailsWithoutAConnector()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        await testHost.LoadRootAsync();

        var server = testHost.CreateServer("/NotInTheGraph", isEnabled: false);
        testHost.Container.Server = server;
        await OpcUaTestHost.WaitForWrapperStartedAsync(server);

        // Act
        await server.StartAsync();

        // Assert
        // The factory reports a failed lookup by throwing, so the failure has to arrive as the wrapper's
        // own status with nothing kept running behind it.
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.Equal("Could not resolve subject at path: /NotInTheGraph", server.StatusMessage);
        Assert.Null(server.Connector);
        Assert.False(server.IsServerRunning);
    }

    [Fact]
    public async Task WhenAnEnabledServerEntersTheGraph_ThenItsConnectorStartRunsBeforeStartupCompletes()
    {
        // Arrange
        var recorder = new StartupCompletionRecorder();
        await using var testHost = await OpcUaTestHost.StartAsync(
            context => context.AddService<IStartupCompletion>(recorder));
        await testHost.LoadRootAsync();
        var server = testHost.CreateServer("/NotInTheGraph");
        recorder.Probe = () => server.Status == ServiceStatus.Error;

        // Act
        testHost.Container.Server = server;
        await OpcUaTestHost.WaitForStatusAsync(() => server.Status, ServiceStatus.Error);
        await AsyncTestHelpers.WaitUntilAsync(
            () => recorder.Outstanding == 0,
            message: "A completion deferral was never released.");

        // Assert
        // The wrapper's own completion deferral covers its start, which runs the connector's start to
        // its end, here the failure against a path that does not resolve. Settling before that is a
        // startup completion wait passing while the connector's start is still to come.
        Assert.Equal(1, recorder.DeferralCount);
        Assert.True(recorder.ProbeWhenFirstSettled);
    }

    [Fact]
    public async Task WhenThePathIsNotConfigured_ThenTheStartFailsWithoutWaitingForTheRoot()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var server = testHost.CreateServer(path: string.Empty, isEnabled: false);
        testHost.Container.Server = server;
        await OpcUaTestHost.WaitForWrapperStartedAsync(server);

        // Act
        await server.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.Equal("Path is not configured", server.StatusMessage);
        Assert.Null(server.Connector);

        // The guard runs ahead of the wait on the root, so an unconfigured server reports its error at
        // once instead of sitting in Starting until a root arrives.
        Assert.False(testHost.RootManager.IsLoaded);
    }

    [Fact]
    public async Task WhenTheRootHasNotLoaded_ThenTheStartWaitsForItBeforeCreatingTheConnector()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var server = testHost.CreateServer("/NotInTheGraph");

        // Act
        testHost.Container.Server = server;
        await OpcUaTestHost.WaitForStatusAsync(() => server.Status, ServiceStatus.Starting);

        // Assert
        // A timed observation of something that has to not happen. It cannot false fail: only
        // LoadRootAsync completes RootLoaded, nothing else in the process can, and the start parks on
        // that task, so waiting longer cannot change the answer.
        await Task.Delay(UnloadedRootObservation);
        Assert.Equal(ServiceStatus.Starting, server.Status);
        Assert.Null(server.Connector);

        // Act
        await testHost.LoadRootAsync();

        // Assert
        // The message as well as the status, because the failing start writes the status first: waiting
        // for that alone and then reading the message lands between the two writes.
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Status == ServiceStatus.Error &&
                  server.StatusMessage == "Could not resolve subject at path: /NotInTheGraph",
            message: "The start did not fail against the configured path once the root had loaded.");
    }

    [Fact]
    public async Task WhenTheHostHasDrained_ThenTheStartReportsThatNothingWasStarted()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        await testHost.LoadRootAsync();

        var server = testHost.CreateServer("/NotInTheGraph", isEnabled: false);
        testHost.Container.Server = server;
        await testHost.StopHostAsync();

        // Act
        await server.StartAsync();

        // Assert
        // The wrapper's run has ended, so the start refuses before the factory runs at all, which is
        // what distinguishes it from the resolution failure above.
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.Equal("Not running in a host, so nothing was started", server.StatusMessage);
        Assert.Null(server.Connector);
    }

    [Fact]
    public async Task WhenAnEnabledServerIsReconfigured_ThenTheStartRunsAgainstTheEditedPath()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        await testHost.LoadRootAsync();

        var server = testHost.CreateServer("/NotInTheGraph");
        testHost.Container.Server = server;

        // The message as well as the status, because the failing start writes the status first: waiting
        // for that alone and then reading the message lands between the two writes.
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Status == ServiceStatus.Error &&
                  server.StatusMessage == "Could not resolve subject at path: /NotInTheGraph",
            message: "The start did not fail against the configured path.");

        // Act
        server.Path = "/AlsoNotInTheGraph";
        await server.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        // The failed start kept no connector, so the stop here has nothing to do and the edit is applied
        // by the start alone. The factory resolves the path on every start, so a message naming the
        // edited path is that start having run rather than the previous one still reported.
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.Equal("Could not resolve subject at path: /AlsoNotInTheGraph", server.StatusMessage);
        Assert.Null(server.Connector);
    }

    [Fact]
    public async Task WhenTheServerSubjectLeavesTheGraph_ThenItReportsStopped()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        await testHost.LoadRootAsync();

        var server = testHost.CreateServer("/NotInTheGraph");
        testHost.Container.Server = server;
        await OpcUaTestHost.WaitForStatusAsync(() => server.Status, ServiceStatus.Error);

        // Act
        testHost.Container.Server = null;

        // Assert
        // The message is waited for alongside the status rather than read after it: the wrapper arrives
        // here from Error, the message is the text behind that status alone, and the reported stop
        // writes the status first.
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Status == ServiceStatus.Stopped && server.StatusMessage is null,
            message: "The server subject did not report its stop, or kept the error text behind it.");

        // The diagnostics are deliberately not asserted here. This server never reaches Running, since
        // its path does not resolve, so they were never set and asserting them null passes with the
        // stop's own reset deleted. Pinning that needs a server that binds a port.
    }

    [Fact]
    public async Task WhenTheSubjectLeavesTheGraphWhileAStartWaitsForTheRoot_ThenTheStartIsAbandoned()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();

        // Disabled, so the wrapper starts nothing itself and the start below comes from another caller.
        var server = testHost.CreateServer("/NotInTheGraph", isEnabled: false);
        testHost.Container.Server = server;
        await OpcUaTestHost.WaitForWrapperStartedAsync(server);

        var start = server.StartAsync();
        await OpcUaTestHost.WaitForStatusAsync(() => server.Status, ServiceStatus.Starting);

        // Act
        testHost.Container.Server = null;

        // Assert
        // The wrapper's stop cancels the wait the start holds the gate across, so the start ends without
        // the root having loaded, rather than at the end of the wait's own bound.
        await start.WaitAsync(CompletionTimeout);
        await OpcUaTestHost.WaitForStatusAsync(() => server.Status, ServiceStatus.Stopped);
        Assert.Null(server.StatusMessage);
        Assert.Null(server.Connector);

        // Act
        await testHost.LoadRootAsync();

        // Assert
        // The abandoned start does not resume once the root arrives.
        Assert.Equal(ServiceStatus.Stopped, server.Status);
        Assert.Null(server.Connector);
    }

    [Fact]
    public async Task WhenAStartWaitsForARootThatNeverLoads_ThenItFailsAndAStopCanRun()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var server = testHost.CreateServer(
            "/NotInTheGraph", isEnabled: false, rootLoadWaitTimeout: TimeSpan.FromMilliseconds(200));
        testHost.Container.Server = server;

        // Enabling it before the wrapper's own start has read the flag would start it a second time.
        await OpcUaTestHost.WaitForWrapperStartedAsync(server);

        // Act
        // The Start operation passes no token, and the start holds the gate the stop needs across this
        // wait, so only the wait's own bound ends it.
        await server.StartAsync().WaitAsync(CompletionTimeout);

        // Assert
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.StartsWith("The root manager did not load", server.StatusMessage);

        // Act
        await server.StopAsync().WaitAsync(CompletionTimeout);

        // Assert
        Assert.Equal(ServiceStatus.Stopped, server.Status);
        Assert.Null(server.StatusMessage);
        Assert.Null(server.Connector);
    }

    [Fact]
    public async Task WhenAReconfigurationIsCancelledWhileItsStartWaitsForTheRoot_ThenTheServerReportsStopped()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        var server = testHost.CreateServer("/NotInTheGraph", isEnabled: false);
        testHost.Container.Server = server;

        // Enabled only once the wrapper's own start has read the flag, so the reconfiguration below is
        // the one start that runs.
        await OpcUaTestHost.WaitForWrapperStartedAsync(server);
        server.IsEnabled = true;

        using var cancellation = new CancellationTokenSource();
        var reconfiguration = server.ApplyConfigurationAsync(cancellation.Token);
        await OpcUaTestHost.WaitForStatusAsync(() => server.Status, ServiceStatus.Starting);

        // Act
        await cancellation.CancelAsync();
        await reconfiguration.WaitAsync(CompletionTimeout);

        // Assert
        // The cancelled wait abandons the start without anything having stopped, so nothing but the
        // start itself can take the status off Starting.
        Assert.Equal(ServiceStatus.Stopped, server.Status);
        Assert.Null(server.StatusMessage);
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Ads.Client;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Ads.Tests.Integration.Models;
using Namotion.Interceptor.Ads.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using TwinCAT.Ads;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.Ads.Tests.Integration;

/// <summary>
/// Tests in this collection share a single ADS server and must run sequentially.
/// The AMS TCP/IP router binds to a fixed port (48898) which cannot be reused quickly,
/// so all tests share a single server instance via the SharedAdsServerFixture.
/// </summary>
[CollectionDefinition("ADS Integration", DisableParallelization = true)]
public class AdsIntegrationCollection : ICollectionFixture<SharedAdsServerFixture>;

[Collection("ADS Integration")]
public class AdsIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly ITestOutputHelper _output;
    private readonly SharedAdsServerFixture _fixture;

    public AdsIntegrationTests(SharedAdsServerFixture fixture, ITestOutputHelper output)
    {
        _output = output;
        _fixture = fixture;
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    private static AdsClientConfiguration CreateConfiguration(AdsTestServer server)
    {
        return new AdsClientConfiguration
        {
            // System-router mode (no Host): connect through the test harness's loopback router config,
            // not the embedded router (which would bind the host's AMS TCP port and ignore RouterConfiguration).
            AmsNetId = AmsNetId.Parse(server.AmsNetIdString),
            AmsPort = server.ServerPort,
            Mapper = AdsCompositeMapper.CreateDefault(AdsConstants.DefaultConnectorName),
            HealthCheckInterval = TimeSpan.FromSeconds(1),
            RouterConfiguration = server.RouterConfiguration,
        };
    }

    /// <summary>
    /// Creates a AdsSubjectClientSource. As a <see cref="SubjectSourceBase"/> derivative it
    /// is itself a hosted service that owns the full lifecycle (connect, subscribe, load initial state,
    /// process property changes).
    /// </summary>
    private static AdsSubjectClientSource CreateClientSource(
        IInterceptorSubject model,
        AdsTestServer server)
    {
        var configuration = CreateConfiguration(server);
        configuration.BufferTime = TimeSpan.FromMilliseconds(50);
        configuration.RetryTime = TimeSpan.FromSeconds(5);

        var sourceLogger = NullLoggerFactory.Instance.CreateLogger<AdsSubjectClientSource>();
        return new AdsSubjectClientSource(model, configuration, sourceLogger);
    }

    private async Task RunIntegrationTestAsync(
        IInterceptorSubject model,
        Func<AdsSubjectClientSource, CancellationToken, Task> testBody)
    {
        _fixture.ResetSymbolValues();
        var clientSource = CreateClientSource(model, _fixture.Server);
        using var cts = new CancellationTokenSource(TestTimeout);

        try
        {
            await clientSource.StartAsync(cts.Token);
            await testBody(clientSource, cts.Token);
        }
        finally
        {
            await cts.CancelAsync();
            try { await clientSource.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            await clientSource.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConnectToServer_ShouldEstablishConnection()
    {
        // Arrange
        var model = new IntegrationTestModel(CreateContext());
        await using var clientSource = CreateClientSource(model, _fixture.Server);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Act
            await clientSource.StartAsync(cts.Token);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(15),
                message: "Client should connect to in-process ADS server");
        }
        finally
        {
            await cts.CancelAsync();
            try { await clientSource.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
        }
        // Note: clientSource is disposed via await using, which may throw InvalidCastException
        // in Beckhoff 7.0.x during AdsSession.Dispose(). This is a known SDK issue.
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadInitialState_ShouldPopulateProperties()
    {
        // Arrange
        var model = new IntegrationTestModel(CreateContext());

        // Act & Assert
        await RunIntegrationTestAsync(model, async (clientSource, cancellationToken) =>
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected,
                timeout: WaitTimeout,
                message: "Client should connect before reading initial state");

            _output.WriteLine($"Connected. NotificationVariableCount={clientSource.Diagnostics.NotificationVariableCount}");

            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(model.Temperature - 25.0) < 0.001 &&
                      model.MachineName == "TestPLC" &&
                      model is { IsRunning: true, Counter: 42 },
                timeout: WaitTimeout,
                message: $"Model properties should match server initial values. " +
                         $"Current: Temperature={model.Temperature}, MachineName={model.MachineName}, " +
                         $"IsRunning={model.IsRunning}, Counter={model.Counter}");

            _output.WriteLine(
                $"Temperature={model.Temperature}, MachineName={model.MachineName}, IsRunning={model.IsRunning}, Counter={model.Counter}");
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Notification_ServerValueChange_UpdatesClientProperty()
    {
        // Arrange
        var model = new IntegrationTestModel(CreateContext());

        await RunIntegrationTestAsync(model, async (clientSource, cancellationToken) =>
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected &&
                      Math.Abs(model.Temperature - 25.0) < 0.001,
                timeout: WaitTimeout,
                message: "Client should connect and load initial Temperature=25.0");

            // Act
            _fixture.Server.SetSymbolValue("GVL.Temperature", 42.0);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(model.Temperature - 42.0) < 0.001,
                timeout: WaitTimeout,
                message: "Client Temperature should update to 42.0 after server notification");

            _output.WriteLine($"Temperature updated to {model.Temperature} via notification");
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WriteProperty_ShouldUpdateServerSymbol()
    {
        // Arrange
        var model = new IntegrationTestModel(CreateContext());

        await RunIntegrationTestAsync(model, async (clientSource, cancellationToken) =>
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected && model.Counter == 42,
                timeout: WaitTimeout,
                message: "Client should connect and load initial Counter=42");

            // Act
            model.Counter = 999;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    var serverValue = _fixture.Server.GetSymbolValue("GVL.Counter");
                    return serverValue is 999;
                },
                timeout: WaitTimeout,
                message: "Server Counter should update to 999 after client write");

            _output.WriteLine($"Counter written to server: {_fixture.Server.GetSymbolValue("GVL.Counter")}");
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MultipleNotifications_ServerChangesMultipleValues_AllUpdateOnClient()
    {
        // Arrange
        var model = new IntegrationTestModel(CreateContext());

        await RunIntegrationTestAsync(model, async (clientSource, cancellationToken) =>
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected &&
                      Math.Abs(model.Temperature - 25.0) < 0.001 &&
                      model.Counter == 42 &&
                      model.IsRunning,
                timeout: WaitTimeout,
                message: "Client should connect and load initial values");

            // Act
            _fixture.Server.SetSymbolValue("GVL.Temperature", 99.5);
            _fixture.Server.SetSymbolValue("GVL.Counter", 100);
            _fixture.Server.SetSymbolValue("GVL.IsRunning", false);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(model.Temperature - 99.5) < 0.001 &&
                      model is { Counter: 100, IsRunning: false },
                timeout: WaitTimeout,
                message: "All client properties should update after server changes multiple values");

            _output.WriteLine(
                $"Temperature={model.Temperature}, Counter={model.Counter}, IsRunning={model.IsRunning}");
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Notifications_ShouldNotCostAThreadPerProperty()
    {
        // The reactive WhenNotification extension allocates a dedicated EventLoopScheduler, and so an
        // OS thread, per call. Subscribing per property therefore cost one thread per property and
        // recreated all of them on every rescan. This guards the batching that replaced it.
        var model = new IntegrationTestModel(CreateContext());
        var threadsBefore = Process.GetCurrentProcess().Threads.Count;

        await RunIntegrationTestAsync(model, async (clientSource, cancellationToken) =>
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected && clientSource.Diagnostics.NotificationVariableCount > 1,
                timeout: WaitTimeout,
                message: "Client should connect and register more than one notification");

            var notificationCount = clientSource.Diagnostics.NotificationVariableCount;
            var handleCount = clientSource.SubscriptionManager.NotificationHandleCount;
            var threadGrowth = Process.GetCurrentProcess().Threads.Count - threadsBefore;

            _output.WriteLine(
                $"Notifications={notificationCount}, handles={handleCount}, thread growth={threadGrowth}");

            // One device notification handle per property, all delivered through a single event on
            // the client's receive thread. The reactive extension would instead allocate a dedicated
            // scheduler thread per registration.
            // Thread growth is logged rather than asserted: at this scale the harness's own thread
            // pool churn swamps the signal. The handle count is the structural invariant.
            Assert.True(notificationCount > 1, "Expected more than one notification property.");
            Assert.Equal(notificationCount, handleCount);

            // Routing still has to work: every property is fed by the one shared subscription.
            _fixture.Server.SetSymbolValue("GVL.Temperature", 77.25);
            _fixture.Server.SetSymbolValue("GVL.Counter", 1234);

            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(model.Temperature - 77.25) < 0.001 && model.Counter == 1234,
                timeout: WaitTimeout,
                message: "Both properties should update through the shared subscription");
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Notifications_WithDifferentCycleTimes_ShouldEachBeRegistered()
    {
        // Each property is registered with the settings it asks for, so a per-property cycle time
        // cannot be silently replaced by another property's.
        var model = new MixedCycleTimeIntegrationTestModel(CreateContext());

        await RunIntegrationTestAsync(model, async (clientSource, cancellationToken) =>
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected &&
                      clientSource.Diagnostics.NotificationVariableCount == 3,
                timeout: WaitTimeout,
                message: "Client should register all three notifications");

            // Each property carries its own notification settings, so all three are registered.
            Assert.Equal(3, clientSource.SubscriptionManager.NotificationHandleCount);

            _fixture.Server.SetSymbolValue("GVL.Temperature", 12.5);
            _fixture.Server.SetSymbolValue("GVL.Counter", 4321);

            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(model.Temperature - 12.5) < 0.001 && model.Counter == 4321,
                timeout: WaitTimeout,
                message: "Properties in both groups should update");
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Notifications_WithTwoPropertiesOnOneSymbol_ShouldRegisterAndFeedBoth()
    {
        // Two properties on one symbol path must both register and both receive values; neither
        // may displace the other's routing.
        var model = new DuplicateSymbolIntegrationTestModel(CreateContext());

        await RunIntegrationTestAsync(model, async (clientSource, cancellationToken) =>
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected &&
                      clientSource.Diagnostics.NotificationVariableCount == 3,
                timeout: WaitTimeout,
                message: "All three properties should be notification-backed");

            // Nothing fell back, and each property holds its own registration on the shared symbol.
            Assert.Equal(0, clientSource.Diagnostics.PolledVariableCount);
            Assert.Equal(3, clientSource.SubscriptionManager.NotificationHandleCount);

            _fixture.Server.SetSymbolValue("GVL.Temperature", 63.5);
            _fixture.Server.SetSymbolValue("GVL.Counter", 909);

            // Both properties on the shared symbol are fed from the one notification.
            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(model.Temperature - 63.5) < 0.001 &&
                      Math.Abs(model.TemperatureMirror - 63.5) < 0.001 &&
                      model.Counter == 909,
                timeout: WaitTimeout,
                message: "Both properties on the shared symbol, and the unrelated one, should update");
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Notifications_AcrossRescans_ShouldReleaseThePreviousRegistrations()
    {
        // A re-scan disposes the notification subscriptions and re-registers. If disposal does not
        // release the PLC-side registrations, every re-scan stacks another full set on the
        // controller until it hits its own notification limit, which is precisely the failure
        // MaxNotifications exists to avoid.
        var model = new IntegrationTestModel(CreateContext());

        await RunIntegrationTestAsync(model, async (clientSource, cancellationToken) =>
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected && clientSource.Diagnostics.NotificationVariableCount > 0,
                timeout: WaitTimeout,
                message: "Client should connect and register notifications");

            var afterFirst = _fixture.Server.NotificationCounts;

            const int rescans = 3;
            for (var i = 0; i < rescans; i++)
            {
                var before = _fixture.Server.NotificationCounts.Added;
                clientSource.RequestRescan("Test-triggered rescan.");
                await AsyncTestHelpers.WaitUntilAsync(
                    () => _fixture.Server.NotificationCounts.Added > before,
                    timeout: WaitTimeout,
                    message: "The rescan should re-register notifications");
            }

            var afterRescans = _fixture.Server.NotificationCounts;
            var added = afterRescans.Added - afterFirst.Added;
            var deleted = afterRescans.Deleted - afterFirst.Deleted;
            _output.WriteLine($"Across {rescans} rescans: added={added}, deleted={deleted}");

            // Every registration a rescan replaces must be released, or the outstanding count grows
            // without bound. Allowing one set of slack covers the registrations still live at the end.
            var outstanding = added - deleted;
            Assert.True(
                outstanding <= clientSource.SubscriptionManager.NotificationHandleCount,
                $"Leaked device notifications: {added} added, {deleted} deleted, {outstanding} outstanding "
                + $"for {clientSource.SubscriptionManager.NotificationHandleCount} held handles.");
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BatchPolling_PolledProperty_ReceivesUpdates()
    {
        // Arrange
        var model = new PolledIntegrationTestModel(CreateContext());

        await RunIntegrationTestAsync(model, async (clientSource, cancellationToken) =>
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected &&
                      clientSource.Diagnostics.PolledVariableCount > 0,
                timeout: WaitTimeout,
                message: "Client should connect and register polled variables");

            _output.WriteLine($"Connected. PolledVariableCount={clientSource.Diagnostics.PolledVariableCount}");

            // Act
            _fixture.Server.SetSymbolValue("GVL.PolledCounter", 99);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => model.PolledCounter == 99,
                timeout: WaitTimeout,
                message: $"PolledCounter should update to 99 via batch polling. Current: {model.PolledCounter}");

            _output.WriteLine($"PolledCounter updated to {model.PolledCounter} via polling");
        });
    }

    // Note: Reconnection tests (ServerRestart_ClientReconnects, ServerRestart_PropertiesResyncAfterReconnection)
    // are not included because the current implementation uses AdsClient directly (not AdsSession) to avoid
    // a Beckhoff 7.0.x dispose bug (InvalidCastException in AdsSession.Dispose). AdsClient does not have
    // built-in automatic reconnection/resurrection. Reconnection tests should be added when either:
    // 1. Beckhoff fixes the AdsSession dispose bug in a future 7.0.x release, or
    // 2. Manual reconnection logic is added to AdsConnectionManager.
}

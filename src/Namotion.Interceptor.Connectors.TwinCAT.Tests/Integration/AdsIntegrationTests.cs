using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Connectors.TwinCAT.Tests.Integration.Models;
using Namotion.Interceptor.Connectors.TwinCAT.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;
using Xunit.Abstractions;


namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Integration;

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
            Host = "127.0.0.1",
            AmsNetId = server.AmsNetIdString,
            AmsPort = server.ServerPort,
            PathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, '.'),
            HealthCheckInterval = TimeSpan.FromSeconds(1),
        };
    }

    /// <summary>
    /// Creates a TwinCatSubjectClientSource and a SubjectSourceBackgroundService that handles the
    /// full lifecycle: connect, subscribe, load initial state, and process property changes.
    /// </summary>
    private static (TwinCatSubjectClientSource ClientSource, SubjectSourceBackgroundService BackgroundService)
        CreateClientWithBackgroundService(
            IInterceptorSubject model,
            AdsTestServer server,
            IInterceptorSubjectContext context)
    {
        var configuration = CreateConfiguration(server);
        var sourceLogger = NullLoggerFactory.Instance.CreateLogger<TwinCatSubjectClientSource>();
        var serviceLogger = NullLoggerFactory.Instance.CreateLogger<SubjectSourceBackgroundService>();

        var clientSource = new TwinCatSubjectClientSource(model, configuration, sourceLogger);

        var backgroundService = new SubjectSourceBackgroundService(
            clientSource,
            context,
            serviceLogger,
            bufferTime: TimeSpan.FromMilliseconds(50),
            retryTime: TimeSpan.FromSeconds(5));

        return (clientSource, backgroundService);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConnectToServer_ShouldEstablishConnection()
    {
        // Arrange
        _fixture.ResetSymbolValues();
        var server = _fixture.Server;

        var context = CreateContext();
        var model = new IntegrationTestModel(context);
        var configuration = CreateConfiguration(server);
        var logger = NullLoggerFactory.Instance.CreateLogger<TwinCatSubjectClientSource>();

        await using var clientSource = new TwinCatSubjectClientSource(model, configuration, logger);

        var propertyWriter = new SubjectPropertyWriter(
            clientSource,
            flushRetryQueueAsync: null,
            logger);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var subscription = await clientSource.StartListeningAsync(propertyWriter, cts.Token);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientSource.Diagnostics.IsConnected,
            timeout: TimeSpan.FromSeconds(15),
            message: "Client should connect to in-process ADS server");

        subscription?.Dispose();
        // Note: clientSource is disposed via await using, which may throw InvalidCastException
        // in Beckhoff 7.0.x during AdsSession.Dispose(). This is a known SDK issue.
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadInitialState_ShouldPopulateProperties()
    {
        // Arrange: server starts with known initial values
        _fixture.ResetSymbolValues();
        var server = _fixture.Server;

        var context = CreateContext();
        var model = new IntegrationTestModel(context);

        var (clientSource, backgroundService) = CreateClientWithBackgroundService(model, server, context);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Act: start the background service (handles full lifecycle: connect, subscribe, load, change processing)
            await backgroundService.StartAsync(cts.Token);

            // Wait for connection
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(15),
                message: "Client should connect before reading initial state");

            _output.WriteLine($"Connected. NotificationVariableCount={clientSource.Diagnostics.NotificationVariableCount}");

            // Assert: model properties should be populated from server initial values via
            // AdsTransMode.OnChange notifications (which deliver the initial value on subscription)
            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(model.Temperature - 25.0) < 0.001 &&
                      model.MachineName == "TestPLC" &&
                      model.IsRunning &&
                      model.Counter == 42,
                timeout: TimeSpan.FromSeconds(15),
                message: $"Model properties should match server initial values. " +
                         $"Current: Temperature={model.Temperature}, MachineName={model.MachineName}, " +
                         $"IsRunning={model.IsRunning}, Counter={model.Counter}");

            _output.WriteLine(
                $"Temperature={model.Temperature}, MachineName={model.MachineName}, IsRunning={model.IsRunning}, Counter={model.Counter}");
        }
        finally
        {
            await cts.CancelAsync();
            try { await backgroundService.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            backgroundService.Dispose();
            await clientSource.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Notification_ServerValueChange_UpdatesClientProperty()
    {
        // Arrange: server starts with Temperature=25.0
        _fixture.ResetSymbolValues();
        var server = _fixture.Server;

        var context = CreateContext();
        var model = new IntegrationTestModel(context);

        var (clientSource, backgroundService) = CreateClientWithBackgroundService(model, server, context);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Start the background service
            await backgroundService.StartAsync(cts.Token);

            // Wait for connection and initial state
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected &&
                      Math.Abs(model.Temperature - 25.0) < 0.001,
                timeout: TimeSpan.FromSeconds(15),
                message: "Client should connect and load initial Temperature=25.0");

            // Act: server changes the Temperature value
            server.SetSymbolValue("GVL.Temperature", 42.0);

            // Assert: client model should receive the updated value via notification
            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(model.Temperature - 42.0) < 0.001,
                timeout: TimeSpan.FromSeconds(15),
                message: "Client Temperature should update to 42.0 after server notification");

            _output.WriteLine($"Temperature updated to {model.Temperature} via notification");
        }
        finally
        {
            await cts.CancelAsync();
            try { await backgroundService.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            backgroundService.Dispose();
            await clientSource.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WriteProperty_ShouldUpdateServerSymbol()
    {
        // Arrange: server starts with known values
        _fixture.ResetSymbolValues();
        var server = _fixture.Server;

        var context = CreateContext();
        var model = new IntegrationTestModel(context);

        var (clientSource, backgroundService) = CreateClientWithBackgroundService(model, server, context);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Start the background service (handles connect + subscribe + change processing)
            await backgroundService.StartAsync(cts.Token);

            // Wait for connection and initial state
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected && model.Counter == 42,
                timeout: TimeSpan.FromSeconds(15),
                message: "Client should connect and load initial Counter=42");

            // Act: change model property (triggers change tracking -> ChangeQueueProcessor -> WriteChangesAsync)
            model.Counter = 999;

            // Assert: server should receive the updated value
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    var serverValue = server.GetSymbolValue("GVL.Counter");
                    return serverValue is int intValue && intValue == 999;
                },
                timeout: TimeSpan.FromSeconds(15),
                message: "Server Counter should update to 999 after client write");

            _output.WriteLine($"Counter written to server: {server.GetSymbolValue("GVL.Counter")}");
        }
        finally
        {
            await cts.CancelAsync();
            try { await backgroundService.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            backgroundService.Dispose();
            await clientSource.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MultipleNotifications_ServerChangesMultipleValues_AllUpdateOnClient()
    {
        // Arrange: server starts with known initial values
        _fixture.ResetSymbolValues();
        var server = _fixture.Server;

        var context = CreateContext();
        var model = new IntegrationTestModel(context);

        var (clientSource, backgroundService) = CreateClientWithBackgroundService(model, server, context);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Start the background service
            await backgroundService.StartAsync(cts.Token);

            // Wait for connection and initial state
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected &&
                      Math.Abs(model.Temperature - 25.0) < 0.001 &&
                      model.Counter == 42 &&
                      model.IsRunning,
                timeout: TimeSpan.FromSeconds(15),
                message: "Client should connect and load initial values");

            // Act: server changes multiple values
            server.SetSymbolValue("GVL.Temperature", 99.5);
            server.SetSymbolValue("GVL.Counter", 100);
            server.SetSymbolValue("GVL.IsRunning", false);

            // Assert: all model properties should update via notifications
            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(model.Temperature - 99.5) < 0.001 &&
                      model.Counter == 100 &&
                      !model.IsRunning,
                timeout: TimeSpan.FromSeconds(15),
                message: "All client properties should update after server changes multiple values");

            _output.WriteLine(
                $"Temperature={model.Temperature}, Counter={model.Counter}, IsRunning={model.IsRunning}");
        }
        finally
        {
            await cts.CancelAsync();
            try { await backgroundService.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            backgroundService.Dispose();
            await clientSource.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BatchPolling_PolledProperty_ReceivesUpdates()
    {
        // Arrange: server starts with PolledCounter=0
        _fixture.ResetSymbolValues();
        var server = _fixture.Server;

        var context = CreateContext();
        var model = new PolledIntegrationTestModel(context);

        var (clientSource, backgroundService) = CreateClientWithBackgroundService(model, server, context);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Start the background service
            await backgroundService.StartAsync(cts.Token);

            // Wait for connection and initial value via polling
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientSource.Diagnostics.IsConnected &&
                      clientSource.Diagnostics.PolledVariableCount > 0,
                timeout: TimeSpan.FromSeconds(15),
                message: "Client should connect and register polled variables");

            _output.WriteLine($"Connected. PolledVariableCount={clientSource.Diagnostics.PolledVariableCount}");

            // Act: server changes the polled value
            server.SetSymbolValue("GVL.PolledCounter", 99);

            // Assert: model should receive the updated value via batch polling
            await AsyncTestHelpers.WaitUntilAsync(
                () => model.PolledCounter == 99,
                timeout: TimeSpan.FromSeconds(15),
                message: $"PolledCounter should update to 99 via batch polling. Current: {model.PolledCounter}");

            _output.WriteLine($"PolledCounter updated to {model.PolledCounter} via polling");
        }
        finally
        {
            await cts.CancelAsync();
            try { await backgroundService.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            backgroundService.Dispose();
            await clientSource.DisposeAsync();
        }
    }

    // Note: Reconnection tests (ServerRestart_ClientReconnects, ServerRestart_PropertiesResyncAfterReconnection)
    // are not included because the current implementation uses AdsClient directly (not AdsSession) to avoid
    // a Beckhoff 7.0.x dispose bug (InvalidCastException in AdsSession.Dispose). AdsClient does not have
    // built-in automatic reconnection/resurrection. Reconnection tests should be added when either:
    // 1. Beckhoff fixes the AdsSession dispose bug in a future 7.0.x release, or
    // 2. Manual reconnection logic is added to AdsConnectionManager.
}

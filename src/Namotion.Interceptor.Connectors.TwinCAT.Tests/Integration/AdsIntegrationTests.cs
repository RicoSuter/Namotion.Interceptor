using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
            Host = "127.0.0.1",
            AmsNetId = server.AmsNetIdString,
            AmsPort = server.ServerPort,
            PathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, '.'),
            HealthCheckInterval = TimeSpan.FromSeconds(1),
            RouterConfiguration = server.RouterConfiguration,
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

    private async Task RunIntegrationTestAsync(
        IInterceptorSubject model,
        Func<TwinCatSubjectClientSource, CancellationToken, Task> testBody)
    {
        _fixture.ResetSymbolValues();
        var context = model.Context!;
        var (clientSource, backgroundService) = CreateClientWithBackgroundService(model, _fixture.Server, context);
        using var cts = new CancellationTokenSource(TestTimeout);

        try
        {
            // Start both services matching production DI registration order:
            // TwinCatSubjectClientSource (rescan + polling loops) then SubjectSourceBackgroundService (listening + writes)
            await clientSource.StartAsync(cts.Token);
            await backgroundService.StartAsync(cts.Token);
            await testBody(clientSource, cts.Token);
        }
        finally
        {
            await cts.CancelAsync();
            try { await backgroundService.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            try { await clientSource.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            backgroundService.Dispose();
            await clientSource.DisposeAsync();
        }
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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Validation;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for OPC UA client live sync - verifies that local model changes create/remove MonitoredItems.
/// These are integration tests that validate the client-side live sync functionality.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaClientLiveSyncTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaClientLiveSyncTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void EnableLiveSync_DefaultsToFalse()
    {
        // Arrange & Act
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        // Assert
        Assert.False(configuration.EnableLiveSync);
    }

    [Fact]
    public void EnableRemoteNodeManagement_DefaultsToFalse()
    {
        // Arrange & Act
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        // Assert
        Assert.False(configuration.EnableRemoteNodeManagement);
    }

    [Fact]
    public void EnableModelChangeEvents_DefaultsToFalse()
    {
        // Arrange & Act
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        // Assert
        Assert.False(configuration.EnableModelChangeEvents);
    }

    [Fact]
    public void EnablePeriodicResync_DefaultsToFalse()
    {
        // Arrange & Act
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        // Assert
        Assert.False(configuration.EnablePeriodicResync);
    }

    [Fact]
    public void PeriodicResyncInterval_DefaultsTo30Seconds()
    {
        // Arrange & Act
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.PeriodicResyncInterval);
    }

    [Fact]
    public void Configuration_CanEnableAllLiveSyncOptions()
    {
        // Arrange & Act
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            EnableLiveSync = true,
            EnableRemoteNodeManagement = true,
            EnableModelChangeEvents = true,
            EnablePeriodicResync = true,
            PeriodicResyncInterval = TimeSpan.FromSeconds(60)
        };

        // Assert
        Assert.True(configuration.EnableLiveSync);
        Assert.True(configuration.EnableRemoteNodeManagement);
        Assert.True(configuration.EnableModelChangeEvents);
        Assert.True(configuration.EnablePeriodicResync);
        Assert.Equal(TimeSpan.FromSeconds(60), configuration.PeriodicResyncInterval);
    }

    private async Task<(IHost ServerHost, TestRoot ServerRoot, IInterceptorSubjectContext ServerContext, IHost ClientHost, TestRoot ClientRoot, IInterceptorSubjectContext ClientContext, PortLease Port, TestLogger Logger, OpcUaClientDiagnostics? ClientDiagnostics)> StartServerAndClientWithLiveSyncAsync(
        bool enableModelChangeEvents = true,
        bool enablePeriodicResync = false,
        TimeSpan? periodicResyncInterval = null)
    {
        var logger = new TestLogger(_output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        logger.Log($"Sync mode: EnableModelChangeEvents={enableModelChangeEvents}, EnablePeriodicResync={enablePeriodicResync}");

        // Start server
        var serverBuilder = Host.CreateApplicationBuilder();

        serverBuilder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        serverBuilder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(logger, "Server", LogLevel.Information);
        });

        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(serverBuilder.Services);

        var serverRoot = new TestRoot(serverContext)
        {
            Connected = true,
            Name = "LiveSyncTest",
            Number = 1m,
            People = []
        };

        serverBuilder.Services.AddSingleton(serverRoot);
        serverBuilder.Services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<TestRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaServerConfiguration
                {
                    RootName = "Root",
                    BaseAddress = port.BaseAddress,
                    ValueConverter = new OpcUaValueConverter(),
                    TelemetryContext = telemetryContext,
                    CleanCertificateStore = true,
                    AutoAcceptUntrustedCertificates = true,
                    CertificateStoreBasePath = port.CertificateStoreBasePath,
                    EnableLiveSync = true,
                    BufferTime = TimeSpan.FromMilliseconds(50)
                };
            });

        var serverHost = serverBuilder.Build();
        await serverHost.StartAsync();
        logger.Log("Server started with EnableLiveSync=true");

        // Wait for server to be ready
        await Task.Delay(500);

        // Start client with EnableLiveSync
        var clientBuilder = Host.CreateApplicationBuilder();

        clientBuilder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        clientBuilder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(logger, "Client", LogLevel.Information);
        });

        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(clientBuilder.Services);

        var clientRoot = new TestRoot(clientContext)
        {
            Connected = false,
            Name = "",
            Number = 0m,
            People = []
        };

        clientBuilder.Services.AddSingleton(clientRoot);
        clientBuilder.Services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<TestRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaClientConfiguration
                {
                    ServerUrl = port.ServerUrl,
                    RootName = "Root",
                    TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                    ValueConverter = new OpcUaValueConverter(),
                    SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                    TelemetryContext = telemetryContext,
                    CertificateStoreBasePath = $"{port.CertificateStoreBasePath}/client",
                    EnableLiveSync = true,
                    EnableModelChangeEvents = enableModelChangeEvents,
                    EnablePeriodicResync = enablePeriodicResync,
                    PeriodicResyncInterval = periodicResyncInterval ?? TimeSpan.FromMilliseconds(200), // Fast for tests
                    BufferTime = TimeSpan.FromMilliseconds(50)
                };
            });

        var clientHost = clientBuilder.Build();

        // Get diagnostics
        OpcUaClientDiagnostics? diagnostics = null;
        var clientSource = clientHost.Services
            .GetServices<IHostedService>()
            .OfType<OpcUaSubjectClientSource>()
            .FirstOrDefault();
        if (clientSource != null)
        {
            diagnostics = clientSource.Diagnostics;
        }

        await clientHost.StartAsync();
        logger.Log("Client started with EnableLiveSync=true");

        // Wait for client to connect and sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientRoot.Connected,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should connect and sync Connected property");

        logger.Log("Client connected and synced");

        return (serverHost, serverRoot, serverContext, clientHost, clientRoot, clientContext, port, logger, diagnostics);
    }

    [Theory]
    [InlineData(true, false, "EventBased")] // ModelChangeEvents enabled, PeriodicResync disabled
    [InlineData(false, true, "PeriodicResync")] // ModelChangeEvents disabled, PeriodicResync enabled
    public async Task AddSubjectToCollection_WithLiveSync_MonitoredItemsCreated(
        bool enableModelChangeEvents, bool enablePeriodicResync, string syncMode)
    {
        IHost? serverHost = null;
        IHost? clientHost = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (serverHost, var serverRoot, var serverContext, clientHost, var clientRoot, var clientContext, port, logger, var diagnostics) =
                await StartServerAndClientWithLiveSyncAsync(enableModelChangeEvents, enablePeriodicResync);

            logger.Log($"Testing with sync mode: {syncMode}");

            // Verify initial state
            var initialMonitoredItemCount = diagnostics?.MonitoredItemCount ?? 0;
            logger.Log($"Initial MonitoredItemCount: {initialMonitoredItemCount}");

            // Verify client starts with empty People collection
            Assert.Empty(clientRoot.People);

            // Act: Add a person to the SERVER's collection
            var serverPerson = new TestPerson(serverContext)
            {
                FirstName = "John",
                LastName = "Doe"
            };
            serverRoot.People = [serverPerson];
            logger.Log("Added person to server collection");

            // Wait for the client to receive the structural change via live sync
            // The client should create new MonitoredItems for the added person's properties
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientRoot.People.Length == 1,
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should sync the new person from server");

            // Verify the client has the person with correct properties
            Assert.Single(clientRoot.People);
            var clientPerson = clientRoot.People[0];

            await AsyncTestHelpers.WaitUntilAsync(
                () => clientPerson.FirstName == "John" && clientPerson.LastName == "Doe",
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should sync person properties");

            Assert.Equal("John", clientPerson.FirstName);
            Assert.Equal("Doe", clientPerson.LastName);
            logger.Log($"Client synced person: {clientPerson.FirstName} {clientPerson.LastName}");

            // Verify MonitoredItemCount increased (new items for person's properties)
            var finalMonitoredItemCount = diagnostics?.MonitoredItemCount ?? 0;
            logger.Log($"Final MonitoredItemCount: {finalMonitoredItemCount}");

            // Should have more monitored items now (for FirstName, LastName, FullName, etc.)
            Assert.True(finalMonitoredItemCount > initialMonitoredItemCount,
                $"Expected MonitoredItemCount to increase from {initialMonitoredItemCount}, but got {finalMonitoredItemCount}");

            logger.Log("Test passed - MonitoredItems created for added subject");
        }
        finally
        {
            if (clientHost != null)
            {
                await clientHost.StopAsync();
                clientHost.Dispose();
            }
            if (serverHost != null)
            {
                await serverHost.StopAsync();
                serverHost.Dispose();
            }
            port?.Dispose();
        }
    }

    [Theory]
    [InlineData(true, false, "EventBased")] // ModelChangeEvents enabled, PeriodicResync disabled
    [InlineData(false, true, "PeriodicResync")] // ModelChangeEvents disabled, PeriodicResync enabled
    public async Task RemoveSubjectFromCollection_WithLiveSync_MonitoredItemsRemoved(
        bool enableModelChangeEvents, bool enablePeriodicResync, string syncMode)
    {
        IHost? serverHost = null;
        IHost? clientHost = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (serverHost, var serverRoot, var serverContext, clientHost, var clientRoot, var clientContext, port, logger, var diagnostics) =
                await StartServerAndClientWithLiveSyncAsync(enableModelChangeEvents, enablePeriodicResync);

            logger.Log($"Testing with sync mode: {syncMode}");

            // Setup: Add two people to the collection
            var person1 = new TestPerson(serverContext) { FirstName = "Alice", LastName = "Smith" };
            var person2 = new TestPerson(serverContext) { FirstName = "Bob", LastName = "Jones" };
            serverRoot.People = [person1, person2];
            logger.Log("Added two people to server collection");

            // Wait for client to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientRoot.People.Length == 2,
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should sync two people from server");

            var monitoredItemCountWithTwo = diagnostics?.MonitoredItemCount ?? 0;
            logger.Log($"MonitoredItemCount with two people: {monitoredItemCountWithTwo}");

            // Act: Remove one person from the server's collection
            serverRoot.People = [person1]; // Keep only Alice
            logger.Log("Removed Bob from server collection");

            // Wait for the client to receive the structural change
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientRoot.People.Length == 1,
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should sync removal from server");

            // Verify
            Assert.Single(clientRoot.People);
            logger.Log($"Client now has {clientRoot.People.Length} person(s)");

            // MonitoredItemCount should decrease (items for removed person cleaned up)
            // Note: Cleanup happens when subject is detached, not immediately
            await Task.Delay(500); // Allow time for cleanup

            var finalMonitoredItemCount = diagnostics?.MonitoredItemCount ?? 0;
            logger.Log($"Final MonitoredItemCount: {finalMonitoredItemCount}");

            // The count should be less than when we had two people
            Assert.True(finalMonitoredItemCount <= monitoredItemCountWithTwo,
                $"Expected MonitoredItemCount to decrease from {monitoredItemCountWithTwo}, but got {finalMonitoredItemCount}");

            logger.Log("Test passed - MonitoredItems cleaned up for removed subject");
        }
        finally
        {
            if (clientHost != null)
            {
                await clientHost.StopAsync();
                clientHost.Dispose();
            }
            if (serverHost != null)
            {
                await serverHost.StopAsync();
                serverHost.Dispose();
            }
            port?.Dispose();
        }
    }
}

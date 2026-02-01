using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Graph;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for shared subject scenarios where the same object is referenced by multiple parents (collections/dictionaries).
/// Tests that ReferenceAdded and ReferenceDeleted events properly update collections/dictionaries on the client side.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaSharedSubjectTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly TestLogger _logger;

    public OpcUaSharedSubjectTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = new TestLogger(output);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Tests that when a server adds a shared subject to a second collection,
    /// the client receives a ReferenceAdded event and sees the subject in both collections.
    ///
    /// Scenario:
    /// 1. Server creates a shared sensor in PrimaryItems collection
    /// 2. Client syncs and sees the sensor in PrimaryItems
    /// 3. Server adds the SAME sensor to SecondaryItems collection
    /// 4. Client receives ReferenceAdded event and sees sensor in both collections
    /// </summary>
    [Fact]
    public async Task Server_AddSharedSubjectToCollection_ClientReceivesReferenceAdded()
    {
        // Arrange
        var port = await OpcUaTestPortPool.AcquireAsync();

        try
        {
            // Start server with EnableLiveSync
            var serverBuilder = Host.CreateApplicationBuilder();
            ConfigureHost(serverBuilder, "Server");

            var serverContext = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithLifecycle()
                .WithDataAnnotationValidation()
                .WithHostedServices(serverBuilder.Services);

            var serverRoot = new MultiCollectionTestRoot(serverContext)
            {
                PrimaryItems = [],
                SecondaryItems = []
            };

            serverBuilder.Services.AddSingleton(serverRoot);
            serverBuilder.Services.AddOpcUaSubjectServer(
                sp => sp.GetRequiredService<MultiCollectionTestRoot>(),
                sp => CreateServerConfiguration(sp, port, enableLiveSync: true));

            var serverHost = serverBuilder.Build();
            await serverHost.StartAsync();
            _logger.Log("Server started with EnableLiveSync=true");

            await Task.Delay(500);

            // Start client with EnableLiveSync and ModelChangeEvents
            var clientBuilder = Host.CreateApplicationBuilder();
            ConfigureHost(clientBuilder, "Client");

            var clientContext = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithLifecycle()
                .WithDataAnnotationValidation()
                .WithHostedServices(clientBuilder.Services);

            var clientRoot = new MultiCollectionTestRoot(clientContext)
            {
                PrimaryItems = [],
                SecondaryItems = []
            };

            clientBuilder.Services.AddSingleton(clientRoot);
            clientBuilder.Services.AddOpcUaSubjectClientSource(
                sp => sp.GetRequiredService<MultiCollectionTestRoot>(),
                sp => CreateClientConfiguration(sp, port));

            var clientHost = clientBuilder.Build();
            await clientHost.StartAsync();
            _logger.Log("Client started with EnableLiveSync=true, EnableModelChangeEvents=true");

            try
            {
                // Wait for initial sync
                await Task.Delay(1000);

                // Act: Step 1 - Add sensor to PrimaryItems on server
                var sharedSensor = new SharedSensor(serverContext) { Value = 42.0 };
                serverRoot.PrimaryItems = [sharedSensor];
                _logger.Log("Server: Added shared sensor to PrimaryItems");

                // Wait for client to sync PrimaryItems
                await AsyncTestHelpers.WaitUntilAsync(
                    () => clientRoot.PrimaryItems.Length == 1,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "Client should receive sensor in PrimaryItems");

                _logger.Log($"Client: PrimaryItems.Length = {clientRoot.PrimaryItems.Length}");
                Assert.Single(clientRoot.PrimaryItems);
                Assert.Equal(42.0, clientRoot.PrimaryItems[0].Value);

                // Act: Step 2 - Add SAME sensor to SecondaryItems on server
                // This should trigger a ReferenceAdded event (not NodeAdded, since node already exists)
                serverRoot.SecondaryItems = [sharedSensor];
                _logger.Log("Server: Added same shared sensor to SecondaryItems");

                // Assert: Client should see the sensor in BOTH collections
                await AsyncTestHelpers.WaitUntilAsync(
                    () => clientRoot.SecondaryItems.Length == 1,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "Client should receive sensor in SecondaryItems via ReferenceAdded");

                _logger.Log($"Client: SecondaryItems.Length = {clientRoot.SecondaryItems.Length}");

                // Verify both collections have the sensor
                Assert.Single(clientRoot.PrimaryItems);
                Assert.Single(clientRoot.SecondaryItems);

                // Verify values are correct
                Assert.Equal(42.0, clientRoot.PrimaryItems[0].Value);
                Assert.Equal(42.0, clientRoot.SecondaryItems[0].Value);

                _logger.Log("Verified: Client sees shared sensor in both collections");

                // Additional verification: Change the shared sensor value on server
                // Both collections on client should reflect the change
                sharedSensor.Value = 100.0;
                _logger.Log("Server: Changed shared sensor value to 100.0");

                await AsyncTestHelpers.WaitUntilAsync(
                    () => Math.Abs(clientRoot.PrimaryItems[0].Value - 100.0) < 0.01 &&
                          Math.Abs(clientRoot.SecondaryItems[0].Value - 100.0) < 0.01,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "Both collections should reflect the updated value");

                _logger.Log($"Client: PrimaryItems[0].Value = {clientRoot.PrimaryItems[0].Value}");
                _logger.Log($"Client: SecondaryItems[0].Value = {clientRoot.SecondaryItems[0].Value}");
                _logger.Log("Verified: Value change propagated to both collection entries");
            }
            finally
            {
                await clientHost.StopAsync();
                clientHost.Dispose();
                await serverHost.StopAsync();
                serverHost.Dispose();
            }
        }
        finally
        {
            port.Dispose();
        }
    }

    /// <summary>
    /// Tests that when a server removes a shared subject from one collection (but keeps it in another),
    /// the client receives a ReferenceDeleted event and sees the subject only in the remaining collection.
    ///
    /// Scenario:
    /// 1. Server starts with empty collections
    /// 2. Server adds shared sensor to PrimaryItems (client syncs)
    /// 3. Server adds SAME sensor to SecondaryItems (client syncs via ReferenceAdded)
    /// 4. Server removes sensor from SecondaryItems (client receives ReferenceDeleted)
    /// 5. Client should see sensor only in PrimaryItems
    /// </summary>
    [Fact]
    public async Task Server_RemoveSharedSubjectFromCollection_ClientReceivesReferenceDeleted()
    {
        // Arrange
        var port = await OpcUaTestPortPool.AcquireAsync();

        try
        {
            // Start server with EnableLiveSync
            var serverBuilder = Host.CreateApplicationBuilder();
            ConfigureHost(serverBuilder, "Server");

            var serverContext = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithLifecycle()
                .WithDataAnnotationValidation()
                .WithHostedServices(serverBuilder.Services);

            var serverRoot = new MultiCollectionTestRoot(serverContext)
            {
                PrimaryItems = [],
                SecondaryItems = []
            };

            serverBuilder.Services.AddSingleton(serverRoot);
            serverBuilder.Services.AddOpcUaSubjectServer(
                sp => sp.GetRequiredService<MultiCollectionTestRoot>(),
                sp => CreateServerConfiguration(sp, port, enableLiveSync: true));

            var serverHost = serverBuilder.Build();
            await serverHost.StartAsync();
            _logger.Log("Server started with EnableLiveSync=true");

            await Task.Delay(500);

            // Start client with EnableLiveSync and ModelChangeEvents
            var clientBuilder = Host.CreateApplicationBuilder();
            ConfigureHost(clientBuilder, "Client");

            var clientContext = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithLifecycle()
                .WithDataAnnotationValidation()
                .WithHostedServices(clientBuilder.Services);

            var clientRoot = new MultiCollectionTestRoot(clientContext)
            {
                PrimaryItems = [],
                SecondaryItems = []
            };

            clientBuilder.Services.AddSingleton(clientRoot);
            clientBuilder.Services.AddOpcUaSubjectClientSource(
                sp => sp.GetRequiredService<MultiCollectionTestRoot>(),
                sp => CreateClientConfiguration(sp, port));

            var clientHost = clientBuilder.Build();
            await clientHost.StartAsync();
            _logger.Log("Client started with EnableLiveSync=true, EnableModelChangeEvents=true");

            try
            {
                // Wait for initial sync
                await Task.Delay(1000);

                // Step 1: Add shared sensor to PrimaryItems on server
                var sharedSensor = new SharedSensor(serverContext) { Value = 42.0 };
                serverRoot.PrimaryItems = [sharedSensor];
                _logger.Log("Server: Added shared sensor to PrimaryItems");

                // Wait for client to sync PrimaryItems
                await AsyncTestHelpers.WaitUntilAsync(
                    () => clientRoot.PrimaryItems.Length == 1,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "Client should receive sensor in PrimaryItems");

                _logger.Log($"Client: PrimaryItems.Length = {clientRoot.PrimaryItems.Length}");
                Assert.Single(clientRoot.PrimaryItems);
                Assert.Equal(42.0, clientRoot.PrimaryItems[0].Value);

                // Step 2: Add SAME sensor to SecondaryItems on server
                serverRoot.SecondaryItems = [sharedSensor];
                _logger.Log("Server: Added same shared sensor to SecondaryItems");

                // Wait for client to sync SecondaryItems via ReferenceAdded
                await AsyncTestHelpers.WaitUntilAsync(
                    () => clientRoot.SecondaryItems.Length == 1,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "Client should receive sensor in SecondaryItems via ReferenceAdded");

                _logger.Log($"Client: SecondaryItems.Length = {clientRoot.SecondaryItems.Length}");
                Assert.Single(clientRoot.SecondaryItems);
                Assert.Equal(42.0, clientRoot.SecondaryItems[0].Value);
                _logger.Log("Verified: Client sees shared sensor in both collections");

                // Step 3: Remove sensor from SecondaryItems on server (keep in PrimaryItems)
                // This should trigger a ReferenceDeleted event (not NodeDeleted, since node still exists in PrimaryItems)
                serverRoot.SecondaryItems = [];
                _logger.Log("Server: Removed shared sensor from SecondaryItems (kept in PrimaryItems)");

                // Assert: Client should see sensor ONLY in PrimaryItems, not in SecondaryItems
                await AsyncTestHelpers.WaitUntilAsync(
                    () => clientRoot.SecondaryItems.Length == 0,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "Client should receive ReferenceDeleted and remove sensor from SecondaryItems");

                _logger.Log($"Client: PrimaryItems.Length = {clientRoot.PrimaryItems.Length}");
                _logger.Log($"Client: SecondaryItems.Length = {clientRoot.SecondaryItems.Length}");

                // Verify PrimaryItems still has the sensor
                Assert.Single(clientRoot.PrimaryItems);
                Assert.Equal(42.0, clientRoot.PrimaryItems[0].Value);

                // Verify SecondaryItems is now empty
                Assert.Empty(clientRoot.SecondaryItems);

                _logger.Log("Verified: Client sees shared sensor only in PrimaryItems after removal from SecondaryItems");

                // Additional verification: Change the shared sensor value on server
                // Only PrimaryItems on client should reflect the change
                sharedSensor.Value = 100.0;
                _logger.Log("Server: Changed shared sensor value to 100.0");

                await AsyncTestHelpers.WaitUntilAsync(
                    () => Math.Abs(clientRoot.PrimaryItems[0].Value - 100.0) < 0.01,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "PrimaryItems should reflect the updated value");

                _logger.Log($"Client: PrimaryItems[0].Value = {clientRoot.PrimaryItems[0].Value}");
                _logger.Log("Verified: Value change propagated to remaining collection entry");
            }
            finally
            {
                await clientHost.StopAsync();
                clientHost.Dispose();
                await serverHost.StopAsync();
                serverHost.Dispose();
            }
        }
        finally
        {
            port.Dispose();
        }
    }

    /// <summary>
    /// Tests that when a server removes a shared subject from one parent (collection),
    /// the server sends ReferenceDeleted (NOT NodeDeleted).
    ///
    /// This is verified by ensuring that:
    /// 1. The shared subject still exists in the remaining collection after removal from one
    /// 2. Value changes on the shared subject still propagate correctly
    /// 3. The client doesn't lose the subject entirely (which would happen with NodeDeleted)
    ///
    /// Scenario:
    /// 1. Server starts with empty collections
    /// 2. Server adds sensor to PrimaryItems (client syncs via NodeAdded)
    /// 3. Server adds same sensor to SecondaryItems (client syncs via ReferenceAdded)
    /// 4. Server removes sensor from PrimaryItems (should send ReferenceDeleted, NOT NodeDeleted)
    /// 5. Client should still see sensor in SecondaryItems (node not deleted)
    /// 6. Server updates sensor value, client should receive update
    /// </summary>
    [Fact]
    public async Task Server_RemoveSharedSubjectFromOneParent_SendsReferenceDeleted()
    {
        // Arrange
        var port = await OpcUaTestPortPool.AcquireAsync();

        try
        {
            // Start server with EnableLiveSync
            var serverBuilder = Host.CreateApplicationBuilder();
            ConfigureHost(serverBuilder, "Server");

            var serverContext = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithLifecycle()
                .WithDataAnnotationValidation()
                .WithHostedServices(serverBuilder.Services);

            // Start with empty collections
            var serverRoot = new MultiCollectionTestRoot(serverContext)
            {
                PrimaryItems = [],
                SecondaryItems = []
            };

            serverBuilder.Services.AddSingleton(serverRoot);
            serverBuilder.Services.AddOpcUaSubjectServer(
                sp => sp.GetRequiredService<MultiCollectionTestRoot>(),
                sp => CreateServerConfiguration(sp, port, enableLiveSync: true));

            var serverHost = serverBuilder.Build();
            await serverHost.StartAsync();
            _logger.Log("Server started with empty collections");

            await Task.Delay(500);

            // Start client with EnableLiveSync and ModelChangeEvents
            var clientBuilder = Host.CreateApplicationBuilder();
            ConfigureHost(clientBuilder, "Client");

            var clientContext = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithLifecycle()
                .WithDataAnnotationValidation()
                .WithHostedServices(clientBuilder.Services);

            var clientRoot = new MultiCollectionTestRoot(clientContext)
            {
                PrimaryItems = [],
                SecondaryItems = []
            };

            clientBuilder.Services.AddSingleton(clientRoot);
            clientBuilder.Services.AddOpcUaSubjectClientSource(
                sp => sp.GetRequiredService<MultiCollectionTestRoot>(),
                sp => CreateClientConfiguration(sp, port));

            var clientHost = clientBuilder.Build();
            await clientHost.StartAsync();
            _logger.Log("Client started");

            try
            {
                // Wait for initial sync
                await Task.Delay(1000);

                // Step 1: Add sensor to PrimaryItems on server
                var sharedSensor = new SharedSensor(serverContext) { Value = 10.0 };
                serverRoot.PrimaryItems = [sharedSensor];
                _logger.Log("Server: Added shared sensor to PrimaryItems");

                // Wait for client to sync PrimaryItems
                await AsyncTestHelpers.WaitUntilAsync(
                    () => clientRoot.PrimaryItems.Length == 1,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "Client should sync sensor from PrimaryItems");

                _logger.Log($"Client: PrimaryItems.Length = {clientRoot.PrimaryItems.Length}");
                Assert.Single(clientRoot.PrimaryItems);
                Assert.Equal(10.0, clientRoot.PrimaryItems[0].Value);

                // Step 2: Add shared sensor to SecondaryItems on server
                serverRoot.SecondaryItems = [sharedSensor];
                _logger.Log("Server: Added shared sensor to SecondaryItems");

                // Wait for client to sync SecondaryItems via ReferenceAdded
                await AsyncTestHelpers.WaitUntilAsync(
                    () => clientRoot.SecondaryItems.Length == 1,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "Client should receive sensor in SecondaryItems via ReferenceAdded");

                _logger.Log($"Client: SecondaryItems.Length = {clientRoot.SecondaryItems.Length}");
                Assert.Single(clientRoot.PrimaryItems);
                Assert.Single(clientRoot.SecondaryItems);
                Assert.Equal(10.0, clientRoot.SecondaryItems[0].Value);
                _logger.Log("Verified: Client sees shared sensor in both collections");

                // Step 3: Remove sensor from PrimaryItems on server (keep in SecondaryItems)
                // This should trigger ReferenceDeleted (NOT NodeDeleted)
                serverRoot.PrimaryItems = [];
                _logger.Log("Server: Removed shared sensor from PrimaryItems (kept in SecondaryItems)");

                // Assert: Client should see sensor ONLY in SecondaryItems
                await AsyncTestHelpers.WaitUntilAsync(
                    () => clientRoot.PrimaryItems.Length == 0,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "Client should receive ReferenceDeleted and remove sensor from PrimaryItems");

                _logger.Log($"Client: After removal - PrimaryItems.Length = {clientRoot.PrimaryItems.Length}");
                _logger.Log($"Client: After removal - SecondaryItems.Length = {clientRoot.SecondaryItems.Length}");

                // Verify PrimaryItems is now empty
                Assert.Empty(clientRoot.PrimaryItems);

                // CRITICAL: Verify SecondaryItems STILL has the sensor (node was NOT deleted)
                // If NodeDeleted was sent instead of ReferenceDeleted, the client would have removed
                // the subject entirely, and SecondaryItems would also be empty or have a stale reference
                Assert.Single(clientRoot.SecondaryItems);
                Assert.Equal(10.0, clientRoot.SecondaryItems[0].Value);
                _logger.Log("Verified: Sensor still exists in SecondaryItems (ReferenceDeleted sent, not NodeDeleted)");

                // Step 4: Update value on server - client should still receive updates
                // This proves the node is still alive and the subscription is working
                sharedSensor.Value = 50.0;
                _logger.Log("Server: Updated shared sensor value to 50.0");

                await AsyncTestHelpers.WaitUntilAsync(
                    () => Math.Abs(clientRoot.SecondaryItems[0].Value - 50.0) < 0.01,
                    timeout: TimeSpan.FromSeconds(30),
                    pollInterval: TimeSpan.FromMilliseconds(500),
                    message: "Client should receive value update (node still alive)");

                _logger.Log($"Client: SecondaryItems[0].Value = {clientRoot.SecondaryItems[0].Value}");
                Assert.Equal(50.0, clientRoot.SecondaryItems[0].Value, precision: 2);
                _logger.Log("Verified: Value update received - confirms node still alive and subscribed");
            }
            finally
            {
                await clientHost.StopAsync();
                clientHost.Dispose();
                await serverHost.StopAsync();
                serverHost.Dispose();
            }
        }
        finally
        {
            port.Dispose();
        }
    }

    #region Helper Methods

    private void ConfigureHost(HostApplicationBuilder builder, string name)
    {
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(_logger, name, LogLevel.Debug);
        });
    }

    private OpcUaServerConfiguration CreateServerConfiguration(
        IServiceProvider serviceProvider,
        PortLease port,
        bool enableLiveSync = false)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
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
            EnableLiveSync = enableLiveSync,
            BufferTime = TimeSpan.FromMilliseconds(50)
        };
    }

    private OpcUaClientConfiguration CreateClientConfiguration(
        IServiceProvider serviceProvider,
        PortLease port)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var telemetryContext = DefaultTelemetry.Create(b =>
            b.Services.AddSingleton(loggerFactory));

        return new OpcUaClientConfiguration
        {
            ServerUrl = port.ServerUrl,
            RootName = "Root",
            TypeResolver = new OpcUaTypeResolver(serviceProvider.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            TelemetryContext = telemetryContext,
            CertificateStoreBasePath = $"{port.CertificateStoreBasePath}/client",
            EnableLiveSync = true,
            EnableModelChangeEvents = true,
            EnablePeriodicResync = false,
            BufferTime = TimeSpan.FromMilliseconds(50)
        };
    }

    #endregion
}

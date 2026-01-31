using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Validation;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

#region Test Models for Flat Collection Integration Tests

/// <summary>
/// Root model with flat collection structure for server-side testing.
/// </summary>
[InterceptorSubject]
[OpcUaNode("FlatRoot")]
public partial class FlatSyncRoot
{
    public FlatSyncRoot()
    {
        Connected = false;
        DeviceName = "";
        Sensors = [];
    }

    /// <summary>
    /// Connection status - used to verify basic sync works.
    /// </summary>
    [OpcUaNode("Connected")]
    public partial bool Connected { get; set; }

    /// <summary>
    /// Device name for identification.
    /// </summary>
    [OpcUaNode("DeviceName")]
    public partial string DeviceName { get; set; }

    /// <summary>
    /// Collection with Flat structure - items appear directly under root (no container node).
    /// </summary>
    [OpcUaReference("HasComponent", CollectionStructure = CollectionNodeStructure.Flat)]
    public partial FlatSensor[] Sensors { get; set; }
}

/// <summary>
/// Sensor item for flat collection testing.
/// </summary>
[InterceptorSubject]
[OpcUaNode("FlatSensor")]
public partial class FlatSensor
{
    public FlatSensor()
    {
        SensorId = "";
        Value = 0.0;
    }

    [OpcUaNode("SensorId")]
    public partial string SensorId { get; set; }

    [OpcUaNode("Value")]
    public partial double Value { get; set; }
}

#endregion

/// <summary>
/// Encapsulates a running server with browse session for flat collection tests.
/// </summary>
public class FlatCollectionServerContext : IAsyncDisposable
{
    public required IHost ServerHost { get; init; }
    public required FlatSyncRoot ServerRoot { get; init; }
    public required IInterceptorSubjectContext ServerContext { get; init; }
    public required PortLease Port { get; init; }
    public required TestLogger Logger { get; init; }
    public required ISession BrowseSession { get; init; }

    public async ValueTask DisposeAsync()
    {
        await BrowseSession.CloseAsync();
        BrowseSession.Dispose();
        await ServerHost.StopAsync();
        ServerHost.Dispose();
        Port.Dispose();
    }
}

/// <summary>
/// Integration tests for flat collection mode (CollectionNodeStructure.Flat).
///
/// IMPLEMENTATION STATUS:
/// - Server-side flat collection creation: WORKING
/// - Server-side live sync (add/remove items): WORKING
/// - Client-side initial sync for flat collections: NOT YET IMPLEMENTED
///   (OpcUaSubjectLoader cannot map "Sensors[0]" to "Sensors" property)
/// - Client-side live sync for flat collections: NOT YET IMPLEMENTED
///
/// These tests verify the server-side implementation is correct by browsing the OPC UA address space.
/// Client-side tests are skipped until OpcUaSubjectLoader is enhanced to handle flat collections.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaFlatCollectionTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly TestLogger _logger;

    public OpcUaFlatCollectionTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = new TestLogger(output);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    #region Server-Side Flat Collection Structure Tests

    /// <summary>
    /// Tests that flat collection creates items directly under root (no container node).
    /// </summary>
    [Fact]
    public async Task FlatCollection_ServerCreatesNodesDirectlyUnderParent()
    {
        await using var ctx = await StartFlatCollectionServerAsync(
            initialSensors: [
                ("Sensor1", 25.5),
                ("Sensor2", 30.0)
            ]);

        // Verify server has correct initial state
        Assert.Equal(2, ctx.ServerRoot.Sensors.Length);
        Assert.Equal("Sensor1", ctx.ServerRoot.Sensors[0].SensorId);
        Assert.Equal("Sensor2", ctx.ServerRoot.Sensors[1].SensorId);
        _logger.Log("Server has 2 sensors with correct IDs");

        // Verify OPC UA structure - items should be directly under root (no container node)
        var rootNodeId = await FindRootNodeIdAsync(ctx.BrowseSession, "FlatRoot");
        var rootChildren = await BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId);
        _logger.Log($"Root children: {string.Join(", ", rootChildren.Select(c => c.BrowseName.Name))}");

        // Should NOT have a "Sensors" container node
        Assert.DoesNotContain(rootChildren, c => c.BrowseName.Name == "Sensors");
        _logger.Log("Verified: No 'Sensors' container node exists");

        // Should have items directly under root with indexed names
        Assert.Contains(rootChildren, c => c.BrowseName.Name.Contains("[0]"));
        Assert.Contains(rootChildren, c => c.BrowseName.Name.Contains("[1]"));
        _logger.Log("Verified: Items are directly under root with indexed names");

        // Verify each item has expected properties
        var item0Ref = rootChildren.FirstOrDefault(c => c.BrowseName.Name.Contains("[0]"));
        Assert.NotNull(item0Ref);
        var item0NodeId = ExpandedNodeId.ToNodeId(item0Ref.NodeId, ctx.BrowseSession.NamespaceUris);
        var item0Children = await BrowseChildNodesAsync(ctx.BrowseSession, item0NodeId);
        Assert.Contains(item0Children, c => c.BrowseName.Name == "SensorId");
        Assert.Contains(item0Children, c => c.BrowseName.Name == "Value");
        _logger.Log("Verified: Flat collection item has expected properties");
    }

    /// <summary>
    /// Tests that server can add items to flat collection and OPC UA structure updates.
    /// </summary>
    [Fact]
    public async Task FlatCollection_ServerAddsItem_NodeAppearsDirectlyUnderParent()
    {
        await using var ctx = await StartFlatCollectionServerAsync(
            initialSensors: [("InitialSensor", 20.0)],
            enableLiveSync: true);

        var rootNodeId = await FindRootNodeIdAsync(ctx.BrowseSession, "FlatRoot");

        // Verify initial state
        var initialChildren = await BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId);
        var initialItemCount = initialChildren.Count(c => c.BrowseName.Name.Contains("["));
        Assert.Equal(1, initialItemCount);
        _logger.Log($"Initial state: {initialItemCount} sensor item(s)");

        // Act: Server adds a new sensor
        var newSensor = new FlatSensor(ctx.ServerContext)
        {
            SensorId = "NewSensor",
            Value = 42.0
        };
        ctx.ServerRoot.Sensors = [.. ctx.ServerRoot.Sensors, newSensor];
        _logger.Log("Server added NewSensor");

        // Wait for OPC UA structure to update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId).GetAwaiter().GetResult();
                var itemCount = children.Count(c => c.BrowseName.Name.Contains("["));
                return itemCount == 2;
            },
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "OPC UA should show 2 flat collection items");

        var updatedChildren = await BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId);
        _logger.Log($"Updated children: {string.Join(", ", updatedChildren.Select(c => c.BrowseName.Name))}");

        // Verify structure is still flat (no container node created)
        Assert.DoesNotContain(updatedChildren, c => c.BrowseName.Name == "Sensors");
        Assert.Contains(updatedChildren, c => c.BrowseName.Name.Contains("[0]"));
        Assert.Contains(updatedChildren, c => c.BrowseName.Name.Contains("[1]"));
        _logger.Log("Verified: Flat structure maintained after add");
    }

    /// <summary>
    /// Tests that server can remove items from flat collection and OPC UA structure updates.
    /// </summary>
    [Fact]
    public async Task FlatCollection_ServerRemovesItem_NodeRemovedFromOpcUa()
    {
        await using var ctx = await StartFlatCollectionServerAsync(
            initialSensors: [
                ("Sensor1", 10.0),
                ("Sensor2", 20.0),
                ("Sensor3", 30.0)
            ],
            enableLiveSync: true);

        var rootNodeId = await FindRootNodeIdAsync(ctx.BrowseSession, "FlatRoot");

        // Verify initial state
        var initialChildren = await BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId);
        var initialItemCount = initialChildren.Count(c => c.BrowseName.Name.Contains("["));
        Assert.Equal(3, initialItemCount);
        _logger.Log($"Initial state: {initialItemCount} sensor items");

        // Act: Server removes Sensor2 (middle item)
        ctx.ServerRoot.Sensors = ctx.ServerRoot.Sensors.Where(s => s.SensorId != "Sensor2").ToArray();
        _logger.Log("Server removed Sensor2");

        // Wait for OPC UA structure to update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId).GetAwaiter().GetResult();
                var itemCount = children.Count(c => c.BrowseName.Name.Contains("["));
                return itemCount == 2;
            },
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "OPC UA should show 2 flat collection items after removal");

        var updatedChildren = await BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId);
        var updatedItemCount = updatedChildren.Count(c => c.BrowseName.Name.Contains("["));
        _logger.Log($"After removal: {updatedItemCount} sensor items");

        Assert.Equal(2, updatedItemCount);
        _logger.Log("Verified: Item was removed from flat collection");
    }

    /// <summary>
    /// Tests that server can replace entire flat collection and OPC UA structure updates.
    /// </summary>
    [Fact]
    public async Task FlatCollection_ServerReplacesCollection_OpcUaReflectsNewItems()
    {
        await using var ctx = await StartFlatCollectionServerAsync(
            initialSensors: [
                ("OldSensor1", 1.0),
                ("OldSensor2", 2.0)
            ],
            enableLiveSync: true);

        var rootNodeId = await FindRootNodeIdAsync(ctx.BrowseSession, "FlatRoot");

        // Verify initial state
        var initialChildren = await BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId);
        var initialItemCount = initialChildren.Count(c => c.BrowseName.Name.Contains("["));
        Assert.Equal(2, initialItemCount);
        _logger.Log($"Initial state: {initialItemCount} sensor items");

        // Act: Server replaces entire collection with 3 new items
        ctx.ServerRoot.Sensors = [
            new FlatSensor(ctx.ServerContext) { SensorId = "NewA", Value = 100.0 },
            new FlatSensor(ctx.ServerContext) { SensorId = "NewB", Value = 200.0 },
            new FlatSensor(ctx.ServerContext) { SensorId = "NewC", Value = 300.0 }
        ];
        _logger.Log("Server replaced collection with 3 new sensors");

        // Wait for OPC UA structure to update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId).GetAwaiter().GetResult();
                var itemCount = children.Count(c => c.BrowseName.Name.Contains("["));
                return itemCount == 3;
            },
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "OPC UA should show 3 flat collection items after replacement");

        var updatedChildren = await BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId);
        var updatedItemCount = updatedChildren.Count(c => c.BrowseName.Name.Contains("["));
        _logger.Log($"After replacement: {updatedItemCount} sensor items");

        Assert.Equal(3, updatedItemCount);
        Assert.DoesNotContain(updatedChildren, c => c.BrowseName.Name == "Sensors");
        _logger.Log("Verified: Collection replaced and structure remains flat");
    }

    /// <summary>
    /// Tests that server can clear flat collection to empty and OPC UA structure updates.
    /// </summary>
    [Fact]
    public async Task FlatCollection_ServerClearsCollection_AllItemsRemovedFromOpcUa()
    {
        await using var ctx = await StartFlatCollectionServerAsync(
            initialSensors: [("ToRemove", 50.0)],
            enableLiveSync: true);

        var rootNodeId = await FindRootNodeIdAsync(ctx.BrowseSession, "FlatRoot");

        // Verify initial state
        var initialChildren = await BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId);
        var initialItemCount = initialChildren.Count(c => c.BrowseName.Name.Contains("["));
        Assert.Equal(1, initialItemCount);
        _logger.Log($"Initial state: {initialItemCount} sensor item");

        // Act: Server clears the collection
        ctx.ServerRoot.Sensors = [];
        _logger.Log("Server cleared the collection");

        // Wait for OPC UA structure to update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId).GetAwaiter().GetResult();
                var itemCount = children.Count(c => c.BrowseName.Name.Contains("["));
                return itemCount == 0;
            },
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "OPC UA should show 0 flat collection items after clear");

        var updatedChildren = await BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId);
        var updatedItemCount = updatedChildren.Count(c => c.BrowseName.Name.Contains("["));
        _logger.Log($"After clear: {updatedItemCount} sensor items");

        Assert.Equal(0, updatedItemCount);
        _logger.Log("Verified: All items removed from flat collection");
    }

    /// <summary>
    /// Tests that property values of items in flat collection are accessible via OPC UA.
    /// </summary>
    [Fact]
    public async Task FlatCollection_PropertyValuesAccessibleViaOpcUa()
    {
        await using var ctx = await StartFlatCollectionServerAsync(
            initialSensors: [("TestSensor", 123.456)],
            enableLiveSync: false);

        var rootNodeId = await FindRootNodeIdAsync(ctx.BrowseSession, "FlatRoot");
        var rootChildren = await BrowseChildNodesAsync(ctx.BrowseSession, rootNodeId);
        var itemRef = rootChildren.FirstOrDefault(c => c.BrowseName.Name.Contains("[0]"));
        Assert.NotNull(itemRef);

        var itemNodeId = ExpandedNodeId.ToNodeId(itemRef.NodeId, ctx.BrowseSession.NamespaceUris);
        var itemChildren = await BrowseChildNodesAsync(ctx.BrowseSession, itemNodeId);

        // Find and read SensorId
        var sensorIdRef = itemChildren.FirstOrDefault(c => c.BrowseName.Name == "SensorId");
        Assert.NotNull(sensorIdRef);
        var sensorIdNodeId = ExpandedNodeId.ToNodeId(sensorIdRef.NodeId, ctx.BrowseSession.NamespaceUris);
        var sensorIdValue = await ctx.BrowseSession.ReadValueAsync(sensorIdNodeId);
        Assert.Equal("TestSensor", sensorIdValue.Value);
        _logger.Log($"SensorId value: {sensorIdValue.Value}");

        // Find and read Value
        var valueRef = itemChildren.FirstOrDefault(c => c.BrowseName.Name == "Value");
        Assert.NotNull(valueRef);
        var valueNodeId = ExpandedNodeId.ToNodeId(valueRef.NodeId, ctx.BrowseSession.NamespaceUris);
        var valueValue = await ctx.BrowseSession.ReadValueAsync(valueNodeId);
        Assert.Equal(123.456, (double)valueValue.Value, precision: 3);
        _logger.Log($"Value: {valueValue.Value}");
    }

    #endregion

    #region Client-Side Sync Tests (Skipped - Not Yet Implemented)

    // NOTE: Client-side sync for flat collections is NOT yet implemented.
    // The OpcUaSubjectLoader does not currently handle flat collection items
    // because nodes like "Sensors[0]" cannot be matched to the "Sensors" property.
    // These tests document the expected behavior once implemented.

    /// <summary>
    /// Tests that client can sync flat collection from server.
    /// SKIPPED: OpcUaSubjectLoader cannot map "PropertyName[index]" to collection properties.
    /// </summary>
    [Fact(Skip = "Flat collection client-side sync not implemented - OpcUaSubjectLoader cannot map indexed browse names to collection properties")]
    public async Task FlatCollection_ClientSyncsFromServer()
    {
        // This test would verify that the client receives flat collection items
        // once OpcUaSubjectLoader is enhanced to:
        // 1. Detect browse names like "Sensors[0]", "Sensors[1]" etc.
        // 2. Group them by property name prefix ("Sensors")
        // 3. Match the grouped items to the "Sensors" collection property
        // 4. Load each item as a collection element
        await Task.CompletedTask;
    }

    /// <summary>
    /// Tests that client receives updates when server adds to flat collection.
    /// SKIPPED: Requires client-side flat collection sync to be implemented first.
    /// </summary>
    [Fact(Skip = "Flat collection client-side sync not implemented")]
    public async Task FlatCollection_ClientReceivesServerAdditions()
    {
        await Task.CompletedTask;
    }

    /// <summary>
    /// Tests that client receives updates when server removes from flat collection.
    /// SKIPPED: Requires client-side flat collection sync to be implemented first.
    /// </summary>
    [Fact(Skip = "Flat collection client-side sync not implemented")]
    public async Task FlatCollection_ClientReceivesServerRemovals()
    {
        await Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    private async Task<FlatCollectionServerContext> StartFlatCollectionServerAsync(
        (string id, double value)[] initialSensors,
        bool enableLiveSync = true)
    {
        var port = await OpcUaTestPortPool.AcquireAsync();

        // Start server
        var serverBuilder = Host.CreateApplicationBuilder();
        ConfigureHost(serverBuilder, "FlatCollectionServer");

        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(serverBuilder.Services);

        var serverRoot = new FlatSyncRoot(serverContext)
        {
            Connected = true,
            DeviceName = "FlatCollectionTestDevice",
            Sensors = initialSensors
                .Select(s => new FlatSensor(serverContext) { SensorId = s.id, Value = s.value })
                .ToArray()
        };

        serverBuilder.Services.AddSingleton(serverRoot);
        serverBuilder.Services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<FlatSyncRoot>(),
            sp => CreateServerConfiguration(sp, port, enableLiveSync));

        var serverHost = serverBuilder.Build();
        await serverHost.StartAsync();
        _logger.Log($"Server started with {initialSensors.Length} sensors, EnableLiveSync={enableLiveSync}");

        await Task.Delay(500);

        // Create browse session for OPC UA structure verification
        var browseSession = await CreateBrowseSessionAsync(port);
        _logger.Log("Browse session created");

        return new FlatCollectionServerContext
        {
            ServerHost = serverHost,
            ServerRoot = serverRoot,
            ServerContext = serverContext,
            Port = port,
            Logger = _logger,
            BrowseSession = browseSession
        };
    }

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
            logging.AddXunit(_logger, name, LogLevel.Information);
        });
    }

    private OpcUaServerConfiguration CreateServerConfiguration(
        IServiceProvider serviceProvider,
        PortLease port,
        bool enableLiveSync)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var telemetryContext = DefaultTelemetry.Create(b =>
            b.Services.AddSingleton(loggerFactory));

        return new OpcUaServerConfiguration
        {
            RootName = "FlatRoot",
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

    private async Task<ISession> CreateBrowseSessionAsync(PortLease port)
    {
        var clientConfig = new ApplicationConfiguration
        {
            ApplicationName = "FlatCollectionBrowseClient",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = "urn:FlatCollectionBrowseClient",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = $"{port.CertificateStoreBasePath}/flat-browse-own",
                    SubjectName = "CN=FlatCollectionBrowseClient"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{port.CertificateStoreBasePath}/issuer"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{port.CertificateStoreBasePath}/trusted"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{port.CertificateStoreBasePath}/rejected"
                },
                AutoAcceptUntrustedCertificates = true
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 30000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 60000
            }
        };

        await clientConfig.ValidateAsync(ApplicationType.Client);

        var endpointConfiguration = EndpointConfiguration.Create(clientConfig);
        var serverUri = new Uri(port.ServerUrl);

        using var discoveryClient = await DiscoveryClient.CreateAsync(
            clientConfig,
            serverUri,
            endpointConfiguration);

        var endpoints = await discoveryClient.GetEndpointsAsync(null);

        var endpointDescription = CoreClientUtils.SelectEndpoint(
            clientConfig,
            serverUri,
            endpoints,
            useSecurity: false,
            NullTelemetryContext.Instance);

        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

        var sessionFactory = new DefaultSessionFactory(NullTelemetryContext.Instance);
        return await sessionFactory.CreateAsync(
            clientConfig,
            endpoint,
            updateBeforeConnect: false,
            checkDomain: false,
            sessionName: "FlatCollectionBrowseClient",
            sessionTimeout: 60000,
            identity: new UserIdentity(new AnonymousIdentityToken()),
            preferredLocales: null);
    }

    private static async Task<NodeId> FindRootNodeIdAsync(ISession session, string rootName)
    {
        var objectsChildren = await BrowseChildNodesAsync(session, ObjectIds.ObjectsFolder);
        var rootRef = objectsChildren.FirstOrDefault(c => c.BrowseName.Name == rootName);
        if (rootRef == null)
        {
            throw new InvalidOperationException($"Root node '{rootName}' not found under ObjectsFolder");
        }
        return ExpandedNodeId.ToNodeId(rootRef.NodeId, session.NamespaceUris);
    }

    private static async Task<IReadOnlyList<ReferenceDescription>> BrowseChildNodesAsync(
        ISession session,
        NodeId parentNodeId)
    {
        var browseDescription = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = parentNodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            }
        };

        var response = await session.BrowseAsync(
            null,
            null,
            0,
            browseDescription,
            CancellationToken.None);

        if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
        {
            return response.Results[0].References;
        }

        return Array.Empty<ReferenceDescription>();
    }

    #endregion
}

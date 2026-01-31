using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for OPC UA structural synchronization - verifies that collection, dictionary, and reference changes
/// update the OPC UA address space correctly.
/// These tests create dedicated server/client instances with EnableLiveSync=true.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaStructuralSyncTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly TestLogger _logger;
    private IHost? _serverHost;
    private ISession? _session;
    private PortLease? _port;
    private TestRoot? _root;
    private IInterceptorSubjectContext? _context;
    private NodeId _rootNodeId = NodeId.Null;
    private NodeId _peopleNodeId = NodeId.Null;
    private NodeId _peopleByNameNodeId = NodeId.Null;

    public OpcUaStructuralSyncTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = new TestLogger(output);
    }

    public async Task InitializeAsync()
    {
        _port = await OpcUaTestPortPool.AcquireAsync();

        var builder = Host.CreateApplicationBuilder();

        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(_logger, "Server", LogLevel.Information);
        });

        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(builder.Services);

        _root = new TestRoot(_context)
        {
            Connected = true,
            Name = "StructuralSyncTest",
            Number = 1m,
            People = [],
            PeopleByName = new Dictionary<string, TestPerson>(),
            Sensor = null // Start with null for reference tests
        };

        builder.Services.AddSingleton(_root);
        builder.Services.AddOpcUaSubjectServer(
            serviceProvider => serviceProvider.GetRequiredService<TestRoot>(),
            serviceProvider =>
            {
                var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaServerConfiguration
                {
                    RootName = "Root",
                    BaseAddress = _port.BaseAddress,
                    ValueConverter = new OpcUaValueConverter(),
                    TelemetryContext = telemetryContext,
                    CleanCertificateStore = true,
                    AutoAcceptUntrustedCertificates = true,
                    CertificateStoreBasePath = _port.CertificateStoreBasePath,
                    EnableLiveSync = true, // Enable live sync for structural changes
                    BufferTime = TimeSpan.FromMilliseconds(50)
                };
            });

        _serverHost = builder.Build();
        await _serverHost.StartAsync();
        _logger.Log("Server started with EnableLiveSync=true");

        // Wait for server to be fully ready
        await Task.Delay(500);

        // Create OPC UA client session
        var clientConfig = CreateClientConfiguration(_port.CertificateStoreBasePath);
        await clientConfig.ValidateAsync(ApplicationType.Client);

        var endpointConfiguration = EndpointConfiguration.Create(clientConfig);
        var serverUri = new Uri(_port.ServerUrl);

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
        _session = await sessionFactory.CreateAsync(
            clientConfig,
            endpoint,
            updateBeforeConnect: false,
            checkDomain: false,
            sessionName: "StructuralSyncTestClient",
            sessionTimeout: 60000,
            identity: new UserIdentity(new AnonymousIdentityToken()),
            preferredLocales: null);

        _logger.Log("Client session connected");

        // Cache commonly used node IDs
        await CacheNodeIdsAsync();
    }

    public async Task DisposeAsync()
    {
        if (_session != null)
        {
            await _session.CloseAsync();
            _session.Dispose();
        }
        if (_serverHost != null)
        {
            await _serverHost.StopAsync();
            _serverHost.Dispose();
        }
        _port?.Dispose();
    }

    private static ApplicationConfiguration CreateClientConfiguration(string certificateStoreBasePath)
    {
        return new ApplicationConfiguration
        {
            ApplicationName = "StructuralSyncTestClient",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = "urn:StructuralSyncTestClient",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = $"{certificateStoreBasePath}/client-own",
                    SubjectName = "CN=StructuralSyncTestClient"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{certificateStoreBasePath}/issuer"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{certificateStoreBasePath}/trusted"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = $"{certificateStoreBasePath}/rejected"
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
    }

    private async Task CacheNodeIdsAsync()
    {
        // Find Root folder
        var objectsChildren = await BrowseNodeChildrenAsync(ObjectIds.ObjectsFolder);
        var rootReference = objectsChildren.FirstOrDefault(child => child.BrowseName.Name == "Root");
        Assert.NotNull(rootReference);
        _rootNodeId = ExpandedNodeId.ToNodeId(rootReference.NodeId, _session!.NamespaceUris);
        _logger.Log($"Found Root node: {_rootNodeId}");

        // Find People folder
        var rootChildren = await BrowseNodeChildrenAsync(_rootNodeId);
        var peopleReference = rootChildren.FirstOrDefault(child => child.BrowseName.Name == "People");
        Assert.NotNull(peopleReference);
        _peopleNodeId = ExpandedNodeId.ToNodeId(peopleReference.NodeId, _session.NamespaceUris);
        _logger.Log($"Found People node: {_peopleNodeId}");

        // Find PeopleByName folder
        var peopleByNameReference = rootChildren.FirstOrDefault(child => child.BrowseName.Name == "PeopleByName");
        Assert.NotNull(peopleByNameReference);
        _peopleByNameNodeId = ExpandedNodeId.ToNodeId(peopleByNameReference.NodeId, _session.NamespaceUris);
        _logger.Log($"Found PeopleByName node: {_peopleByNameNodeId}");
    }

    private async Task<IReadOnlyList<ReferenceDescription>> BrowseNodeChildrenAsync(NodeId parentNodeId)
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

        var response = await _session!.BrowseAsync(
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

    private async Task<bool> NodeExistsAsync(NodeId parentNodeId, string browseName)
    {
        var children = await BrowseNodeChildrenAsync(parentNodeId);
        return children.Any(child => child.BrowseName.Name == browseName);
    }

    private async Task<NodeId?> FindChildNodeIdAsync(NodeId parentNodeId, string browseName)
    {
        var children = await BrowseNodeChildrenAsync(parentNodeId);
        var child = children.FirstOrDefault(c => c.BrowseName.Name == browseName);
        if (child != null)
        {
            return ExpandedNodeId.ToNodeId(child.NodeId, _session!.NamespaceUris);
        }
        return null;
    }

    #region Collection Tests

    [Fact]
    public async Task Collection_AddItem_ClientCanBrowseNewNode()
    {
        // Arrange - verify initial empty state
        var initialChildren = await BrowseNodeChildrenAsync(_peopleNodeId);
        Assert.Empty(initialChildren);
        _logger.Log("Initial People collection is empty");

        // Act - add item to collection
        var newPerson = new TestPerson(_context!)
        {
            FirstName = "John",
            LastName = "Doe"
        };
        _root!.People = [newPerson];
        _logger.Log("Added person to collection");

        // Assert - wait for client to see the new node
        IReadOnlyList<ReferenceDescription>? updatedChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                updatedChildren = BrowseNodeChildrenAsync(_peopleNodeId).GetAwaiter().GetResult();
                return updatedChildren.Count == 1;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see the new node in People collection");

        Assert.NotNull(updatedChildren);
        Assert.Single(updatedChildren);
        Assert.Contains("[0]", updatedChildren[0].BrowseName.Name);
        _logger.Log($"Found new child: {updatedChildren[0].BrowseName}");

        // Verify the new node has expected properties
        var personNodeId = ExpandedNodeId.ToNodeId(updatedChildren[0].NodeId, _session!.NamespaceUris);
        var personChildren = await BrowseNodeChildrenAsync(personNodeId);
        Assert.Contains(personChildren, child => child.BrowseName.Name == "FirstName");
        Assert.Contains(personChildren, child => child.BrowseName.Name == "LastName");
        _logger.Log("Verified person node has expected properties");
    }

    [Fact]
    public async Task Collection_RemoveItem_ClientNoLongerSeesNode()
    {
        // Arrange - add persons to the collection
        var person1 = new TestPerson(_context!) { FirstName = "Alice", LastName = "Smith" };
        var person2 = new TestPerson(_context!) { FirstName = "Bob", LastName = "Jones" };
        _root!.People = [person1, person2];
        _logger.Log("Added two persons to collection");

        // Wait for initial sync
        IReadOnlyList<ReferenceDescription>? initialChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                initialChildren = BrowseNodeChildrenAsync(_peopleNodeId).GetAwaiter().GetResult();
                return initialChildren.Count == 2;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see two initial nodes");

        Assert.NotNull(initialChildren);
        Assert.Equal(2, initialChildren.Count);
        _logger.Log($"Initial children count: {initialChildren.Count}");

        // Act - remove one person
        _root.People = [person1]; // Keep only Alice
        _logger.Log("Removed Bob from collection");

        // Assert - wait for client to see the removal
        IReadOnlyList<ReferenceDescription>? updatedChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                updatedChildren = BrowseNodeChildrenAsync(_peopleNodeId).GetAwaiter().GetResult();
                return updatedChildren.Count == 1;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see the node removed");

        Assert.NotNull(updatedChildren);
        Assert.Single(updatedChildren);
        _logger.Log($"Remaining child: {updatedChildren[0].BrowseName}");
    }

    [Fact]
    public async Task Collection_AddedItemProperties_SyncCorrectly()
    {
        // Arrange - add person with specific values
        var person = new TestPerson(_context!)
        {
            FirstName = "Charlie",
            LastName = "Brown"
        };
        _root!.People = [person];
        _logger.Log("Added person to collection");

        // Wait for node to appear
        IReadOnlyList<ReferenceDescription>? children = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                children = BrowseNodeChildrenAsync(_peopleNodeId).GetAwaiter().GetResult();
                return children.Count == 1;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see the new node");

        Assert.NotNull(children);
        var personNodeId = ExpandedNodeId.ToNodeId(children[0].NodeId, _session!.NamespaceUris);

        // Find and read FirstName
        var personProperties = await BrowseNodeChildrenAsync(personNodeId);
        var firstNameReference = personProperties.FirstOrDefault(p => p.BrowseName.Name == "FirstName");
        Assert.NotNull(firstNameReference);
        var firstNameNodeId = ExpandedNodeId.ToNodeId(firstNameReference.NodeId, _session.NamespaceUris);

        // Assert - verify property value
        var firstNameValue = await _session.ReadValueAsync(firstNameNodeId);
        Assert.Equal("Charlie", firstNameValue.Value);
        _logger.Log($"FirstName value verified: {firstNameValue.Value}");

        // Find and read LastName
        var lastNameReference = personProperties.FirstOrDefault(p => p.BrowseName.Name == "LastName");
        Assert.NotNull(lastNameReference);
        var lastNameNodeId = ExpandedNodeId.ToNodeId(lastNameReference.NodeId, _session.NamespaceUris);
        var lastNameValue = await _session.ReadValueAsync(lastNameNodeId);
        Assert.Equal("Brown", lastNameValue.Value);
        _logger.Log($"LastName value verified: {lastNameValue.Value}");
    }

    #endregion

    #region Dictionary Tests

    [Fact]
    public async Task Dictionary_AddItem_ClientCanBrowseNewNodeWithCorrectKey()
    {
        // Arrange - verify initial empty state
        var initialChildren = await BrowseNodeChildrenAsync(_peopleByNameNodeId);
        Assert.Empty(initialChildren);
        _logger.Log("Initial PeopleByName dictionary is empty");

        // Act - add item to dictionary
        var person = new TestPerson(_context!)
        {
            FirstName = "David",
            LastName = "Wilson"
        };
        _root!.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["david"] = person
        };
        _logger.Log("Added person to dictionary with key 'david'");

        // Assert - wait for client to see the new node
        IReadOnlyList<ReferenceDescription>? updatedChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                updatedChildren = BrowseNodeChildrenAsync(_peopleByNameNodeId).GetAwaiter().GetResult();
                return updatedChildren.Count == 1;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see the new node in PeopleByName dictionary");

        Assert.NotNull(updatedChildren);
        Assert.Single(updatedChildren);
        // Dictionary items should have the key in their browse name
        Assert.Contains("david", updatedChildren[0].BrowseName.Name);
        _logger.Log($"Found new child with key: {updatedChildren[0].BrowseName}");
    }

    [Fact]
    public async Task Dictionary_RemoveItem_ClientNoLongerSeesNode()
    {
        // Arrange - add persons to the dictionary
        var person1 = new TestPerson(_context!) { FirstName = "Eve", LastName = "Anderson" };
        var person2 = new TestPerson(_context!) { FirstName = "Frank", LastName = "Thompson" };
        _root!.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["eve"] = person1,
            ["frank"] = person2
        };
        _logger.Log("Added two persons to dictionary");

        // Wait for initial sync
        IReadOnlyList<ReferenceDescription>? initialChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                initialChildren = BrowseNodeChildrenAsync(_peopleByNameNodeId).GetAwaiter().GetResult();
                return initialChildren.Count == 2;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see two initial nodes");

        Assert.NotNull(initialChildren);
        Assert.Equal(2, initialChildren.Count);
        _logger.Log($"Initial children count: {initialChildren.Count}");

        // Act - remove one person (keep only eve)
        _root.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["eve"] = person1
        };
        _logger.Log("Removed frank from dictionary");

        // Assert - wait for client to see the removal
        IReadOnlyList<ReferenceDescription>? updatedChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                updatedChildren = BrowseNodeChildrenAsync(_peopleByNameNodeId).GetAwaiter().GetResult();
                return updatedChildren.Count == 1;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see the node removed");

        Assert.NotNull(updatedChildren);
        Assert.Single(updatedChildren);
        Assert.Contains("eve", updatedChildren[0].BrowseName.Name);
        _logger.Log($"Remaining child: {updatedChildren[0].BrowseName}");
    }

    #endregion

    #region Reference Tests

    [Fact]
    public async Task Reference_AssignNewPerson_ClientCanBrowseNode()
    {
        // Arrange - verify Person doesn't exist initially (or exists with null-like state)
        var rootChildren = await BrowseNodeChildrenAsync(_rootNodeId);
        var personReference = rootChildren.FirstOrDefault(child => child.BrowseName.Name == "Person");
        _logger.Log($"Initial Person reference: {(personReference != null ? "exists" : "not found")}");

        // Act - assign new Person
        var person = new TestPerson(_context!)
        {
            FirstName = "Grace",
            LastName = "Hopper"
        };
        _root!.Person = person;
        _logger.Log("Assigned new Person reference");

        // Assert - wait for client to see the node with correct properties
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseNodeChildrenAsync(_rootNodeId).GetAwaiter().GetResult();
                var personRef = children.FirstOrDefault(child => child.BrowseName.Name == "Person");
                if (personRef == null) return false;

                var personNodeId = ExpandedNodeId.ToNodeId(personRef.NodeId, _session!.NamespaceUris);
                var personProps = BrowseNodeChildrenAsync(personNodeId).GetAwaiter().GetResult();
                var firstNameRef = personProps.FirstOrDefault(p => p.BrowseName.Name == "FirstName");
                if (firstNameRef == null) return false;

                var firstNameNodeId = ExpandedNodeId.ToNodeId(firstNameRef.NodeId, _session.NamespaceUris);
                var value = _session.ReadValueAsync(firstNameNodeId).GetAwaiter().GetResult();
                return value.Value?.ToString() == "Grace";
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see Person node with correct FirstName");

        _logger.Log("Person reference verified with correct FirstName");
    }

    [Fact]
    public async Task Reference_AssignNull_ClientNoLongerSeesNode()
    {
        // Arrange - assign a Person first
        var person = new TestPerson(_context!)
        {
            FirstName = "Henry",
            LastName = "Ford"
        };
        _root!.Person = person;
        _logger.Log("Assigned initial Person reference");

        // Wait for Person to appear
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseNodeChildrenAsync(_rootNodeId).GetAwaiter().GetResult();
                var personRef = children.FirstOrDefault(child => child.BrowseName.Name == "Person");
                if (personRef == null) return false;

                var personNodeId = ExpandedNodeId.ToNodeId(personRef.NodeId, _session!.NamespaceUris);
                var personProps = BrowseNodeChildrenAsync(personNodeId).GetAwaiter().GetResult();
                return personProps.Any(p => p.BrowseName.Name == "FirstName");
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see initial Person node");

        _logger.Log("Initial Person node verified");

        // Act - assign null to Person
        _root.Person = null!;
        _logger.Log("Assigned null to Person reference");

        // Assert - wait for client to no longer see the Person node (or see it as empty)
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseNodeChildrenAsync(_rootNodeId).GetAwaiter().GetResult();
                var personRef = children.FirstOrDefault(child => child.BrowseName.Name == "Person");
                if (personRef == null) return true; // Node removed entirely

                // Or node exists but has no children (representing null)
                var personNodeId = ExpandedNodeId.ToNodeId(personRef.NodeId, _session!.NamespaceUris);
                var personProps = BrowseNodeChildrenAsync(personNodeId).GetAwaiter().GetResult();
                return personProps.Count == 0;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should no longer see Person node or its children");

        _logger.Log("Person reference cleared successfully");
    }

    [Fact]
    public async Task OpcUaReference_AssignNewSensor_ClientCanBrowseNode()
    {
        // Arrange - Sensor starts as null
        var rootChildren = await BrowseNodeChildrenAsync(_rootNodeId);
        var sensorReference = rootChildren.FirstOrDefault(child => child.BrowseName.Name == "Sensor");
        _logger.Log($"Initial Sensor reference: {(sensorReference != null ? "exists" : "not found")}");

        // Act - assign new Sensor
        var sensor = new TestSensor(_context!)
        {
            Value = 42.5,
            Unit = "°C",
            MinValue = -40.0,
            MaxValue = 85.0
        };
        _root!.Sensor = sensor;
        _logger.Log("Assigned new Sensor reference");

        // Assert - wait for client to see the Sensor node
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseNodeChildrenAsync(_rootNodeId).GetAwaiter().GetResult();
                var sensorRef = children.FirstOrDefault(child => child.BrowseName.Name == "Sensor");
                if (sensorRef == null) return false;

                var sensorNodeId = ExpandedNodeId.ToNodeId(sensorRef.NodeId, _session!.NamespaceUris);
                var sensorProps = BrowseNodeChildrenAsync(sensorNodeId).GetAwaiter().GetResult();
                // Sensor is a Variable node with [OpcUaValue], should have Unit, MinValue, MaxValue
                return sensorProps.Any(p => p.BrowseName.Name == "Unit");
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see Sensor node with properties");

        _logger.Log("Sensor reference verified");
    }

    [Fact]
    public async Task OpcUaReference_ReplaceSensor_ClientSeesNewValue()
    {
        // Arrange - assign a Sensor first with initial value
        var sensor1 = new TestSensor(_context!)
        {
            Value = 50.0,
            Unit = "°F"
        };
        _root!.Sensor = sensor1;
        _logger.Log("Assigned initial Sensor reference with Value=50.0");

        // Wait for Sensor to appear with its children
        NodeId sensorNodeId = NodeId.Null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseNodeChildrenAsync(_rootNodeId).GetAwaiter().GetResult();
                var sensorRef = children.FirstOrDefault(child => child.BrowseName.Name == "Sensor");
                if (sensorRef == null) return false;

                sensorNodeId = ExpandedNodeId.ToNodeId(sensorRef.NodeId, _session!.NamespaceUris);
                var sensorProps = BrowseNodeChildrenAsync(sensorNodeId).GetAwaiter().GetResult();
                return sensorProps.Any(p => p.BrowseName.Name == "Unit");
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see initial Sensor node with children");

        _logger.Log("Initial Sensor node verified with children");

        // Verify initial value
        var initialValue = await _session!.ReadValueAsync(sensorNodeId);
        Assert.Equal(50.0, initialValue.Value);
        _logger.Log($"Initial Sensor Value: {initialValue.Value}");

        // Act - replace with a new Sensor with different value
        var sensor2 = new TestSensor(_context!)
        {
            Value = 99.9,
            Unit = "°C"
        };
        _root.Sensor = sensor2;
        _logger.Log("Replaced Sensor reference with Value=99.9");

        // Assert - wait for client to see the new value
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var value = _session!.ReadValueAsync(sensorNodeId).GetAwaiter().GetResult();
                return Math.Abs((double)value.Value - 99.9) < 0.01;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see updated Sensor value");

        _logger.Log("Sensor reference replaced successfully with new value");
    }

    #endregion
}

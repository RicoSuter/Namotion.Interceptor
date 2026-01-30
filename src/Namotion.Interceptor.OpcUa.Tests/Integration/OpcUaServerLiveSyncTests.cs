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
/// Tests for OPC UA server live sync - verifies that model changes update the OPC UA address space in real-time.
/// These are lifecycle tests that create dedicated server/client instances per test.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaServerLiveSyncTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaServerLiveSyncTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(IHost ServerHost, TestRoot Root, IInterceptorSubjectContext Context, PortLease Port, TestLogger Logger, ISession Session)> StartServerWithLiveSyncAsync()
    {
        var logger = new TestLogger(_output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        var builder = Host.CreateApplicationBuilder();

        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(logger, "Server", LogLevel.Information);
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(builder.Services);

        var root = new TestRoot(context)
        {
            Connected = true,
            Name = "LiveSyncTest",
            Number = 1m,
            People = []
        };

        builder.Services.AddSingleton(root);
        builder.Services.AddOpcUaSubjectServer(
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
                    EnableLiveSync = true, // Enable live sync for these tests
                    BufferTime = TimeSpan.FromMilliseconds(50) // Faster for tests
                };
            });

        var host = builder.Build();
        await host.StartAsync();
        logger.Log("Server started with EnableLiveSync=true");

        // Wait for server to be fully ready
        await Task.Delay(500);

        // Create OPC UA client session
        var clientConfig = CreateClientConfiguration(port.CertificateStoreBasePath);
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
        var session = await sessionFactory.CreateAsync(
            clientConfig,
            endpoint,
            updateBeforeConnect: false,
            checkDomain: false,
            sessionName: "TestClient",
            sessionTimeout: 60000,
            identity: new UserIdentity(new AnonymousIdentityToken()),
            preferredLocales: null);

        logger.Log("Client session connected");

        return (host, root, context, port, logger, session);
    }

    private static ApplicationConfiguration CreateClientConfiguration(string certificateStoreBasePath)
    {
        return new ApplicationConfiguration
        {
            ApplicationName = "TestClient",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = "urn:TestClient",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = $"{certificateStoreBasePath}/client-own",
                    SubjectName = "CN=TestClient"
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

    private async Task<IReadOnlyList<ReferenceDescription>> BrowseNodeChildrenAsync(ISession session, NodeId parentNodeId)
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

    [Fact]
    public async Task AddSubjectToCollection_ClientSeesBrowseChange()
    {
        IHost? serverHost = null;
        ISession? session = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (serverHost, var root, var context, port, logger, session) = await StartServerWithLiveSyncAsync();

            // First browse ObjectsFolder to see what's there
            var objectsChildren = await BrowseNodeChildrenAsync(session, ObjectIds.ObjectsFolder);
            logger.Log($"ObjectsFolder children: {string.Join(", ", objectsChildren.Select(c => c.BrowseName.Name))}");

            // Find Root folder
            var rootRef = objectsChildren.FirstOrDefault(c => c.BrowseName.Name == "Root");
            Assert.NotNull(rootRef);
            var rootNodeId = ExpandedNodeId.ToNodeId(rootRef.NodeId, session.NamespaceUris);
            logger.Log($"Found Root node: {rootNodeId}");

            // Browse Root to find People
            var rootChildren = await BrowseNodeChildrenAsync(session, rootNodeId);
            logger.Log($"Root children: {string.Join(", ", rootChildren.Select(c => c.BrowseName.Name))}");

            // Find People folder
            var peopleRef = rootChildren.FirstOrDefault(c => c.BrowseName.Name == "People");
            Assert.NotNull(peopleRef);
            var peopleNodeId = ExpandedNodeId.ToNodeId(peopleRef.NodeId, session.NamespaceUris);
            logger.Log($"Found People node: {peopleNodeId}");

            var initialChildren = await BrowseNodeChildrenAsync(session, peopleNodeId);
            logger.Log($"Initial children count: {initialChildren.Count}");
            Assert.Empty(initialChildren);

            // Act: Add a subject to the collection
            var newPerson = new TestPerson(context)
            {
                FirstName = "John",
                LastName = "Doe"
            };
            root.People = [newPerson];
            logger.Log("Added person to collection");

            // Wait for live sync to propagate
            IReadOnlyList<ReferenceDescription>? updatedChildren = null;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    updatedChildren = BrowseNodeChildrenAsync(session, peopleNodeId).GetAwaiter().GetResult();
                    return updatedChildren.Count == 1;
                },
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see the new node");

            // Verify the new node
            Assert.NotNull(updatedChildren);
            Assert.Single(updatedChildren);
            Assert.Contains("[0]", updatedChildren[0].BrowseName.Name);
            logger.Log($"Found new child: {updatedChildren[0].BrowseName}");

            // Browse into the new person node and verify properties exist
            var personNodeId = ExpandedNodeId.ToNodeId(updatedChildren[0].NodeId, session.NamespaceUris);
            var personChildren = await BrowseNodeChildrenAsync(session, personNodeId);
            Assert.Contains(personChildren, c => c.BrowseName.Name == "FirstName");
            Assert.Contains(personChildren, c => c.BrowseName.Name == "LastName");
            logger.Log("Verified person node has expected properties");

            logger.Log("Test passed");
        }
        finally
        {
            if (session != null)
            {
                await session.CloseAsync();
                session.Dispose();
            }
            if (serverHost != null)
            {
                await serverHost.StopAsync();
                serverHost.Dispose();
            }
            port?.Dispose();
        }
    }

    [Fact]
    public async Task RemoveSubjectFromCollection_ClientSeesBrowseChange()
    {
        IHost? serverHost = null;
        ISession? session = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (serverHost, var root, var context, port, logger, session) = await StartServerWithLiveSyncAsync();

            // Setup: Add persons to the collection
            var person1 = new TestPerson(context) { FirstName = "Alice", LastName = "Smith" };
            var person2 = new TestPerson(context) { FirstName = "Bob", LastName = "Jones" };
            root.People = [person1, person2];
            logger.Log("Added two persons to collection");

            // Find People node via browsing
            var objectsChildren = await BrowseNodeChildrenAsync(session, ObjectIds.ObjectsFolder);
            var rootRef = objectsChildren.FirstOrDefault(c => c.BrowseName.Name == "Root");
            Assert.NotNull(rootRef);
            var rootNodeId = ExpandedNodeId.ToNodeId(rootRef.NodeId, session.NamespaceUris);

            var rootChildren = await BrowseNodeChildrenAsync(session, rootNodeId);
            var peopleRef = rootChildren.FirstOrDefault(c => c.BrowseName.Name == "People");
            Assert.NotNull(peopleRef);
            var peopleNodeId = ExpandedNodeId.ToNodeId(peopleRef.NodeId, session.NamespaceUris);
            logger.Log($"Found People node: {peopleNodeId}");

            // Wait for initial sync
            IReadOnlyList<ReferenceDescription>? initialChildren = null;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    initialChildren = BrowseNodeChildrenAsync(session, peopleNodeId).GetAwaiter().GetResult();
                    return initialChildren.Count == 2;
                },
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see two initial nodes");

            logger.Log($"Initial children count: {initialChildren!.Count}");
            Assert.Equal(2, initialChildren.Count);

            // Act: Remove one person
            root.People = [person1]; // Keep only Alice
            logger.Log("Removed Bob from collection");

            // Wait for live sync to propagate
            IReadOnlyList<ReferenceDescription>? updatedChildren = null;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    updatedChildren = BrowseNodeChildrenAsync(session, peopleNodeId).GetAwaiter().GetResult();
                    return updatedChildren.Count == 1;
                },
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see the node removed");

            // Verify
            Assert.NotNull(updatedChildren);
            Assert.Single(updatedChildren);
            logger.Log($"Remaining child: {updatedChildren[0].BrowseName}");

            logger.Log("Test passed");
        }
        finally
        {
            if (session != null)
            {
                await session.CloseAsync();
                session.Dispose();
            }
            if (serverHost != null)
            {
                await serverHost.StopAsync();
                serverHost.Dispose();
            }
            port?.Dispose();
        }
    }

    [Fact]
    public async Task ReplaceSubjectReference_ClientSeesBrowseChange()
    {
        IHost? serverHost = null;
        ISession? session = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (serverHost, var root, var context, port, logger, session) = await StartServerWithLiveSyncAsync();

            // Setup: Add initial person reference
            var originalPerson = new TestPerson(context)
            {
                FirstName = "Original",
                LastName = "Person"
            };
            root.Person = originalPerson;
            logger.Log("Set initial Person reference");

            // Find Root node via browsing
            var objectsChildren = await BrowseNodeChildrenAsync(session, ObjectIds.ObjectsFolder);
            var rootRef = objectsChildren.FirstOrDefault(c => c.BrowseName.Name == "Root");
            Assert.NotNull(rootRef);
            var rootNodeId = ExpandedNodeId.ToNodeId(rootRef.NodeId, session.NamespaceUris);

            // Wait for Person node to appear
            NodeId personNodeId = NodeId.Null;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    var rootChildren = BrowseNodeChildrenAsync(session, rootNodeId).GetAwaiter().GetResult();
                    var personRef = rootChildren.FirstOrDefault(c => c.BrowseName.Name == "Person");
                    if (personRef != null)
                    {
                        personNodeId = ExpandedNodeId.ToNodeId(personRef.NodeId, session.NamespaceUris);
                        return true;
                    }
                    return false;
                },
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see initial Person node");

            Assert.NotEqual(NodeId.Null, personNodeId);
            logger.Log($"Found initial Person node: {personNodeId}");

            // Read FirstName from original person
            var personChildren = await BrowseNodeChildrenAsync(session, personNodeId);
            var firstNameRef = personChildren.FirstOrDefault(c => c.BrowseName.Name == "FirstName");
            Assert.NotNull(firstNameRef);
            var originalFirstNameNodeId = ExpandedNodeId.ToNodeId(firstNameRef.NodeId, session.NamespaceUris);
            var originalFirstNameValue = await session.ReadValueAsync(originalFirstNameNodeId);
            Assert.Equal("Original", originalFirstNameValue.Value);
            logger.Log($"Original FirstName: {originalFirstNameValue.Value}");

            // Act: Replace with a new person
            var newPerson = new TestPerson(context)
            {
                FirstName = "Replacement",
                LastName = "Person"
            };
            root.Person = newPerson;
            logger.Log("Replaced Person reference");

            // Wait for live sync to propagate the new value
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    // Browse to find Person and its FirstName
                    var rootChildrenNow = BrowseNodeChildrenAsync(session, rootNodeId).GetAwaiter().GetResult();
                    var personRefNow = rootChildrenNow.FirstOrDefault(c => c.BrowseName.Name == "Person");
                    if (personRefNow == null) return false;

                    var personNodeIdNow = ExpandedNodeId.ToNodeId(personRefNow.NodeId, session.NamespaceUris);
                    var personChildrenNow = BrowseNodeChildrenAsync(session, personNodeIdNow).GetAwaiter().GetResult();
                    var firstNameRefNow = personChildrenNow.FirstOrDefault(c => c.BrowseName.Name == "FirstName");
                    if (firstNameRefNow == null) return false;

                    var firstNameNodeIdNow = ExpandedNodeId.ToNodeId(firstNameRefNow.NodeId, session.NamespaceUris);
                    var value = session.ReadValueAsync(firstNameNodeIdNow).GetAwaiter().GetResult();
                    return value.Value?.ToString() == "Replacement";
                },
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see the new Person's FirstName value");

            // Verify the new person's properties
            var rootChildrenVerify = await BrowseNodeChildrenAsync(session, rootNodeId);
            var personRefVerify = rootChildrenVerify.FirstOrDefault(c => c.BrowseName.Name == "Person");
            Assert.NotNull(personRefVerify);
            var newPersonNodeId = ExpandedNodeId.ToNodeId(personRefVerify.NodeId, session.NamespaceUris);

            var personChildrenVerify = await BrowseNodeChildrenAsync(session, newPersonNodeId);
            var firstNameRefVerify = personChildrenVerify.FirstOrDefault(c => c.BrowseName.Name == "FirstName");
            Assert.NotNull(firstNameRefVerify);
            var newFirstNameNodeIdVerify = ExpandedNodeId.ToNodeId(firstNameRefVerify.NodeId, session.NamespaceUris);
            var newFirstNameValueVerify = await session.ReadValueAsync(newFirstNameNodeIdVerify);
            Assert.Equal("Replacement", newFirstNameValueVerify.Value);
            logger.Log($"New FirstName: {newFirstNameValueVerify.Value}");

            logger.Log("Test passed");
        }
        finally
        {
            if (session != null)
            {
                await session.CloseAsync();
                session.Dispose();
            }
            if (serverHost != null)
            {
                await serverHost.StopAsync();
                serverHost.Dispose();
            }
            port?.Dispose();
        }
    }

    [Fact]
    public async Task CollectionItemRemoved_BrowseNamesReindexed()
    {
        IHost? serverHost = null;
        ISession? session = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (serverHost, var root, var context, port, logger, session) = await StartServerWithLiveSyncAsync();

            // Setup: Add three persons to the collection
            var person1 = new TestPerson(context) { FirstName = "Alice", LastName = "One" };
            var person2 = new TestPerson(context) { FirstName = "Bob", LastName = "Two" };
            var person3 = new TestPerson(context) { FirstName = "Charlie", LastName = "Three" };
            root.People = [person1, person2, person3];
            logger.Log("Added three persons to collection");

            // Find People node via browsing
            var objectsChildren = await BrowseNodeChildrenAsync(session, ObjectIds.ObjectsFolder);
            var rootRef = objectsChildren.FirstOrDefault(c => c.BrowseName.Name == "Root");
            Assert.NotNull(rootRef);
            var rootNodeId = ExpandedNodeId.ToNodeId(rootRef.NodeId, session.NamespaceUris);

            var rootChildren = await BrowseNodeChildrenAsync(session, rootNodeId);
            var peopleRef = rootChildren.FirstOrDefault(c => c.BrowseName.Name == "People");
            Assert.NotNull(peopleRef);
            var peopleNodeId = ExpandedNodeId.ToNodeId(peopleRef.NodeId, session.NamespaceUris);
            logger.Log($"Found People node: {peopleNodeId}");

            // Wait for initial sync
            IReadOnlyList<ReferenceDescription>? initialChildren = null;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    initialChildren = BrowseNodeChildrenAsync(session, peopleNodeId).GetAwaiter().GetResult();
                    return initialChildren.Count == 3;
                },
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see three initial nodes");

            // Verify initial BrowseNames: People[0], People[1], People[2]
            Assert.NotNull(initialChildren);
            logger.Log($"Initial children: {string.Join(", ", initialChildren.Select(c => c.BrowseName.Name))}");
            Assert.Contains(initialChildren, c => c.BrowseName.Name.Contains("[0]"));
            Assert.Contains(initialChildren, c => c.BrowseName.Name.Contains("[1]"));
            Assert.Contains(initialChildren, c => c.BrowseName.Name.Contains("[2]"));

            // Act: Remove the middle person (Bob), keeping Alice and Charlie
            root.People = [person1, person3];
            logger.Log("Removed middle person (Bob) from collection");

            // Wait for live sync to propagate AND for re-indexing to complete
            // Re-indexing happens after structural change processing, not immediately
            IReadOnlyList<ReferenceDescription>? updatedChildren = null;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    updatedChildren = BrowseNodeChildrenAsync(session, peopleNodeId).GetAwaiter().GetResult();
                    // Wait not just for count=2, but for correct BrowseNames (re-indexed)
                    if (updatedChildren.Count != 2) return false;
                    var names = updatedChildren.Select(c => c.BrowseName.Name).ToList();
                    logger.Log($"Checking children: {string.Join(", ", names)}");
                    return names.Any(n => n.Contains("[0]")) && names.Any(n => n.Contains("[1]"));
                },
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see two nodes with re-indexed BrowseNames [0] and [1]");

            // Verify re-indexed BrowseNames: People[0], People[1] (not People[0], People[2])
            Assert.NotNull(updatedChildren);
            logger.Log($"Updated children: {string.Join(", ", updatedChildren.Select(c => c.BrowseName.Name))}");
            Assert.Equal(2, updatedChildren.Count);
            Assert.Contains(updatedChildren, c => c.BrowseName.Name.Contains("[0]"));
            Assert.Contains(updatedChildren, c => c.BrowseName.Name.Contains("[1]"));
            Assert.DoesNotContain(updatedChildren, c => c.BrowseName.Name.Contains("[2]"));

            logger.Log("Test passed - BrowseNames were correctly re-indexed");
        }
        finally
        {
            if (session != null)
            {
                await session.CloseAsync();
                session.Dispose();
            }
            if (serverHost != null)
            {
                await serverHost.StopAsync();
                serverHost.Dispose();
            }
            port?.Dispose();
        }
    }

    [Fact]
    public async Task MultipleAddAndRemove_SequentialOperations_ClientSeesBrowseChanges()
    {
        IHost? serverHost = null;
        ISession? session = null;
        PortLease? port = null;
        TestLogger? logger = null;

        try
        {
            (serverHost, var root, var context, port, logger, session) = await StartServerWithLiveSyncAsync();

            // Find People node via browsing
            var objectsChildren = await BrowseNodeChildrenAsync(session, ObjectIds.ObjectsFolder);
            var rootRef = objectsChildren.FirstOrDefault(c => c.BrowseName.Name == "Root");
            Assert.NotNull(rootRef);
            var rootNodeId = ExpandedNodeId.ToNodeId(rootRef.NodeId, session.NamespaceUris);

            var rootChildren = await BrowseNodeChildrenAsync(session, rootNodeId);
            var peopleRef = rootChildren.FirstOrDefault(c => c.BrowseName.Name == "People");
            Assert.NotNull(peopleRef);
            var peopleNodeId = ExpandedNodeId.ToNodeId(peopleRef.NodeId, session.NamespaceUris);
            logger.Log($"Found People node: {peopleNodeId}");

            // Start empty
            var initialChildren = await BrowseNodeChildrenAsync(session, peopleNodeId);
            Assert.Empty(initialChildren);
            logger.Log("Verified initial empty state");

            // Add first person
            var person1 = new TestPerson(context) { FirstName = "First", LastName = "Person" };
            root.People = [person1];

            await AsyncTestHelpers.WaitUntilAsync(
                () => BrowseNodeChildrenAsync(session, peopleNodeId).GetAwaiter().GetResult().Count == 1,
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see first person");
            logger.Log("First person added");

            // Add second person
            var person2 = new TestPerson(context) { FirstName = "Second", LastName = "Person" };
            root.People = [person1, person2];

            await AsyncTestHelpers.WaitUntilAsync(
                () => BrowseNodeChildrenAsync(session, peopleNodeId).GetAwaiter().GetResult().Count == 2,
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see second person");
            logger.Log("Second person added");

            // Remove first person
            root.People = [person2];

            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    var children = BrowseNodeChildrenAsync(session, peopleNodeId).GetAwaiter().GetResult();
                    return children.Count == 1 && children[0].BrowseName.Name.Contains("[0]");
                },
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see one person with re-indexed BrowseName");
            logger.Log("First person removed, second person re-indexed to [0]");

            // Clear all
            root.People = [];

            await AsyncTestHelpers.WaitUntilAsync(
                () => BrowseNodeChildrenAsync(session, peopleNodeId).GetAwaiter().GetResult().Count == 0,
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should see empty collection");
            logger.Log("Collection cleared");

            logger.Log("Test passed - sequential add/remove operations worked correctly");
        }
        finally
        {
            if (session != null)
            {
                await session.CloseAsync();
                session.Dispose();
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

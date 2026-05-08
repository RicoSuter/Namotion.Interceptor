using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Integration tests for bidirectional structural synchronization between OPC UA server and client.
/// These tests verify that runtime structural changes (add/remove subjects in collections,
/// dictionaries, and references) propagate between server and client.
/// </summary>
[Trait("Category", "Integration")]
public class StructuralSyncTests
{
    private readonly ITestOutputHelper _output;

    public StructuralSyncTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(OpcUaTestServer<TestRoot> Server, OpcUaTestClient<TestRoot> Client, PortLease Port, TestLogger Logger)>
        StartServerAndClientWithStructuralSyncAsync()
    {
        var logger = new TestLogger(_output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        var server = new OpcUaTestServer<TestRoot>(logger, config =>
        {
            config.EnableStructureSynchronization = true;
        });

        await server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Connected = true;
                root.Name = "StructuralSyncServer";
                root.Number = 1m;
                root.ScalarNumbers = [10, 20, 30];
                root.ScalarStrings = ["hello"];

                root.Person = new TestPerson(context)
                {
                    FirstName = "John",
                    LastName = "Doe",
                    Scores = [90.0, 95.0]
                };

                root.People =
                [
                    new TestPerson(context)
                    {
                        FirstName = "Jane",
                        LastName = "Smith",
                        Scores = [88.0]
                    }
                ];

                root.PeopleByName = new Dictionary<string, TestPerson>
                {
                    ["alice"] = new TestPerson(context)
                    {
                        FirstName = "Alice",
                        LastName = "Wonder",
                        Scores = [75.0]
                    }
                };
            },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var client = new OpcUaTestClient<TestRoot>(logger, config =>
        {
            config.EnableStructureSynchronization = true;
        });

        await client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            serverUrl: port.ServerUrl,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        return (server, client, port, logger);
    }

    private async Task<(OpcUaTestServer<TestRoot> Server, OpcUaTestClient<TestRoot> Client, PortLease Port, TestLogger Logger)>
        StartServerAndClientWithBidirectionalSyncAsync()
    {
        var logger = new TestLogger(_output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        var server = new OpcUaTestServer<TestRoot>(logger, config =>
        {
            config.EnableStructureSynchronization = true;
            config.AllowRemoteNodeManagement = true;
            config.SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance);
        });

        await server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Connected = true;
                root.Name = "BidirectionalSyncServer";
                root.Number = 1m;
                root.ScalarNumbers = [10, 20, 30];
                root.ScalarStrings = ["hello"];

                root.Person = new TestPerson(context)
                {
                    FirstName = "John",
                    LastName = "Doe",
                    Scores = [90.0, 95.0]
                };

                root.People =
                [
                    new TestPerson(context)
                    {
                        FirstName = "Jane",
                        LastName = "Smith",
                        Scores = [88.0]
                    }
                ];

                root.PeopleByName = new Dictionary<string, TestPerson>
                {
                    ["alice"] = new TestPerson(context)
                    {
                        FirstName = "Alice",
                        LastName = "Wonder",
                        Scores = [75.0]
                    }
                };
            },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var client = new OpcUaTestClient<TestRoot>(logger, config =>
        {
            config.EnableStructureSynchronization = true;
            config.EnableRemoteNodeManagement = true;
        });

        await client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            serverUrl: port.ServerUrl,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        return (server, client, port, logger);
    }

    // -----------------------------------------------------------------------
    // Server -> Client: Collection
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenServerAddsCollectionItem_ThenClientSeesNewSubject()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial structure to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync (1 item)");

            logger.Log($"Initial client People count: {client.Root.People.Length}");

            // Act: Add a new person to the server's collection
            var newPerson = new TestPerson(((IInterceptorSubject)server.Root).Context)
            {
                FirstName = "Bob",
                LastName = "Builder",
                Scores = [80.0, 85.0]
            };
            server.Root.People = [..server.Root.People, newPerson];
            logger.Log($"Server People count after add: {server.Root.People.Length}");

            // Assert: Client should discover the new subject
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 2,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see the new collection item after server adds it");

            Assert.Equal(2, client.Root.People.Length);

            // Verify the new person's properties are synced
            var clientBob = client.Root.People.FirstOrDefault(
                person => person.FirstName == "Bob");
            Assert.NotNull(clientBob);
            Assert.Equal("Builder", clientBob.LastName);

            logger.Log("Test passed: Client sees new collection item");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenServerRemovesCollectionItem_ThenClientSubjectDetaches()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial structure to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync (1 item)");

            logger.Log($"Initial client People count: {client.Root.People.Length}");

            // Act: Remove the person from the server's collection
            server.Root.People = [];
            logger.Log("Server People emptied");

            // Assert: Client should see the removal
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 0,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see collection item removed after server removes it");

            Assert.Empty(client.Root.People);

            logger.Log("Test passed: Client sees collection item removed");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Server -> Client: Dictionary
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenServerAddsDictionaryEntry_ThenClientSeesNewEntry()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial dictionary to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.PeopleByName != null && client.Root.PeopleByName.Count == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial PeopleByName dictionary should sync (1 entry)");

            logger.Log($"Initial client PeopleByName count: {client.Root.PeopleByName!.Count}");

            // Act: Add a new entry to the server's dictionary.
            // Build a new dictionary rather than mutating in-place so that the
            // property change captures the correct old (1 entry) and new (2 entries) values.
            var updatedDictionary = new Dictionary<string, TestPerson>(server.Root.PeopleByName!)
            {
                ["bob"] = new TestPerson(((IInterceptorSubject)server.Root).Context)
                {
                    FirstName = "Bob",
                    LastName = "Builder",
                    Scores = [80.0]
                }
            };
            server.Root.PeopleByName = updatedDictionary;
            logger.Log($"Server PeopleByName count after add: {server.Root.PeopleByName.Count}");

            // Assert: Client should discover the new dictionary entry
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.PeopleByName != null && client.Root.PeopleByName.Count == 2,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see new dictionary entry after server adds it");

            Assert.Equal(2, client.Root.PeopleByName!.Count);
            Assert.True(client.Root.PeopleByName.ContainsKey("bob"));
            Assert.Equal("Bob", client.Root.PeopleByName["bob"].FirstName);

            logger.Log("Test passed: Client sees new dictionary entry");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenServerRemovesDictionaryEntry_ThenClientEntryDetaches()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial dictionary to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.PeopleByName != null && client.Root.PeopleByName.Count == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial PeopleByName dictionary should sync (1 entry)");

            logger.Log($"Initial client PeopleByName count: {client.Root.PeopleByName!.Count}");

            // Act: Remove the entry from the server's dictionary
            server.Root.PeopleByName = new Dictionary<string, TestPerson>();
            logger.Log("Server PeopleByName emptied");

            // Assert: Client should see the removal
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.PeopleByName == null || client.Root.PeopleByName.Count == 0,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see dictionary entry removed after server removes it");

            Assert.True(
                client.Root.PeopleByName == null || client.Root.PeopleByName.Count == 0,
                "Client dictionary should be empty after server removes all entries");

            logger.Log("Test passed: Client sees dictionary entry removed");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Server -> Client: Reference (single object)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenServerSetsReference_ThenClientSeesNewSubject()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange: Start with Person = null on server
            var logger = new TestLogger(_output);
            port = await OpcUaTestPortPool.AcquireAsync();

            server = new OpcUaTestServer<TestRoot>(logger, config =>
            {
                config.EnableStructureSynchronization = true;
            });

            await server.StartAsync(
                context => new TestRoot(context),
                (context, root) =>
                {
                    root.Connected = true;
                    root.Name = "RefTestServer";
                    root.ScalarNumbers = [1];
                    root.ScalarStrings = [];
                    root.People = [];
                    // Person is intentionally left as default (empty TestPerson from constructor)
                    // We will replace it at runtime
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            client = new OpcUaTestClient<TestRoot>(logger, config =>
            {
                config.EnableStructureSynchronization = true;
            });

            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == "RefTestServer",
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial name should sync");

            logger.Log("Initial sync complete");

            // Act: Set a reference to a new subject on the server
            server.Root.Person = new TestPerson(((IInterceptorSubject)server.Root).Context)
            {
                FirstName = "NewRef",
                LastName = "Person",
                Scores = [99.0]
            };
            logger.Log("Server Person set to new TestPerson");

            // Assert: Client should discover the new referenced subject and its properties.
            // Use null-safe access because Person may be temporarily null during reconciliation
            // (the old subject is detached before the new one is set).
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Person?.FirstName == "NewRef",
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see the new referenced subject's properties after server sets reference");

            Assert.Equal("NewRef", client.Root.Person.FirstName);
            Assert.Equal("Person", client.Root.Person.LastName);

            logger.Log("Test passed: Client sees new referenced subject");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenServerClearsReference_ThenClientSubjectDetaches()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial Person to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Person.FirstName == "John",
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial Person should sync with FirstName='John'");

            logger.Log($"Initial client Person: {client.Root.Person.FirstName} {client.Root.Person.LastName}");

            // Act: Clear the reference on the server by replacing with a default empty person.
            // TestPerson constructor initializes with empty strings, so we detect detach by
            // checking that the previous person's properties are no longer present.
            // Note: Because TestRoot.Person is non-nullable, we replace with a new empty instance
            // rather than setting to null. The structural change is the detach of the old subject
            // and attach of the new one.
            server.Root.Person = new TestPerson(((IInterceptorSubject)server.Root).Context)
            {
                FirstName = "Replaced",
                LastName = "Subject"
            };
            logger.Log("Server Person replaced with new empty subject");

            // Assert: Client should see the new (replacement) subject, not the old "John Doe".
            // Use null-safe access because Person may be temporarily null during reconciliation.
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Person?.FirstName == "Replaced",
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see the replacement subject after server clears/replaces reference");

            Assert.Equal("Replaced", client.Root.Person.FirstName);
            Assert.Equal("Subject", client.Root.Person.LastName);

            logger.Log("Test passed: Client sees replaced referenced subject");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Client -> Server: Collection
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenClientAddsCollectionItem_ThenServerCreatesNodes()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithBidirectionalSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync");

            var initialServerCount = server.Root.People.Length;
            logger.Log($"Initial server People count: {initialServerCount}");

            // Act: Add a new person on the CLIENT side
            var newPerson = new TestPerson(((IInterceptorSubject)client.Root).Context)
            {
                FirstName = "ClientAdded",
                LastName = "Person",
                Scores = [70.0]
            };
            client.Root.People = [..client.Root.People, newPerson];
            logger.Log($"Client People count after add: {client.Root.People.Length}");

            // Assert: Server should see the new subject (structural change).
            // Note: The new subject's property values (FirstName, LastName) are not synced yet
            // because that requires setting up subscriptions for the new subject's properties,
            // which is a follow-up enhancement. For now, we verify the structural change only.
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Length == initialServerCount + 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see the new collection item after client adds it");

            Assert.Equal(initialServerCount + 1, server.Root.People.Length);

            logger.Log("Test passed: Server sees client-added collection item");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenClientRemovesCollectionItem_ThenServerRemovesNodes()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithBidirectionalSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync (1 item)");

            logger.Log($"Initial client People count: {client.Root.People.Length}");

            // Act: Remove the person on the CLIENT side
            client.Root.People = [];
            logger.Log("Client People emptied");

            // Assert: Server should see the removal
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Length == 0,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see collection item removed after client removes it");

            Assert.Empty(server.Root.People);

            logger.Log("Test passed: Server sees client-removed collection item");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Client -> Server: Dictionary
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenClientAddsDictionaryEntry_ThenServerSeesNewEntry()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithBidirectionalSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial dictionary to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.PeopleByName != null && client.Root.PeopleByName.Count == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial PeopleByName dictionary should sync (1 entry)");

            logger.Log($"Initial client PeopleByName count: {client.Root.PeopleByName!.Count}");

            // Act: Add a new entry on the CLIENT side
            var updatedDictionary = new Dictionary<string, TestPerson>(client.Root.PeopleByName!)
            {
                ["bob"] = new TestPerson(((IInterceptorSubject)client.Root).Context)
                {
                    FirstName = "Bob",
                    LastName = "ClientAdded",
                    Scores = [80.0]
                }
            };
            client.Root.PeopleByName = updatedDictionary;
            logger.Log($"Client PeopleByName count after add: {client.Root.PeopleByName.Count}");

            // Assert: Server should see the new entry (structural change).
            // Note: The new entry's property values are not synced yet because that requires
            // setting up subscriptions for the new subject's properties.
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.PeopleByName != null && server.Root.PeopleByName.Count == 2,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see new dictionary entry after client adds it");

            Assert.Equal(2, server.Root.PeopleByName!.Count);
            Assert.True(server.Root.PeopleByName.ContainsKey("bob"));

            logger.Log("Test passed: Server sees client-added dictionary entry");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenClientRemovesDictionaryEntry_ThenServerEntryDetaches()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithBidirectionalSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial dictionary to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.PeopleByName != null && client.Root.PeopleByName.Count == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial PeopleByName dictionary should sync (1 entry)");

            logger.Log($"Initial client PeopleByName count: {client.Root.PeopleByName!.Count}");

            // Act: Remove the entry on the CLIENT side
            client.Root.PeopleByName = new Dictionary<string, TestPerson>();
            logger.Log("Client PeopleByName emptied");

            // Assert: Server should see the removal
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.PeopleByName == null || server.Root.PeopleByName.Count == 0,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see dictionary entry removed after client removes it");

            Assert.True(
                server.Root.PeopleByName == null || server.Root.PeopleByName.Count == 0,
                "Server dictionary should be empty after client removes all entries");

            logger.Log("Test passed: Server sees client-removed dictionary entry");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Client -> Server: Reference (single object)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenClientSetsReference_ThenServerSeesNewSubject()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithBidirectionalSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial Person to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Person.FirstName == "John",
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial Person should sync with FirstName='John'");

            logger.Log($"Initial client Person: {client.Root.Person.FirstName} {client.Root.Person.LastName}");

            // Act: Replace the reference on the CLIENT side
            client.Root.Person = new TestPerson(((IInterceptorSubject)client.Root).Context)
            {
                FirstName = "ClientRef",
                LastName = "Person",
                Scores = [99.0]
            };
            logger.Log("Client Person replaced");

            // Assert: Server should see a different subject instance (structural change).
            // The property values won't match because value sync for newly added subjects
            // requires additional subscription setup, which is a follow-up enhancement.
            // We verify the structural change occurred by checking the Person is not the old one.
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.Person?.FirstName != "John",
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see the referenced subject replaced after client sets reference");

            Assert.NotEqual("John", server.Root.Person.FirstName);

            logger.Log("Test passed: Server sees client-set reference");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenClientClearsReference_ThenServerSubjectDetaches()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithBidirectionalSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial Person to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Person.FirstName == "John",
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial Person should sync with FirstName='John'");

            logger.Log($"Initial client Person: {client.Root.Person.FirstName} {client.Root.Person.LastName}");

            // Act: Replace with an empty person on the CLIENT side
            // (TestRoot.Person is non-nullable, so we replace with a default instance)
            client.Root.Person = new TestPerson(((IInterceptorSubject)client.Root).Context)
            {
                FirstName = "Cleared",
                LastName = "ByClient"
            };
            logger.Log("Client Person cleared/replaced");

            // Assert: Server should see the reference replaced (structural change).
            // The property values won't match because value sync for newly added subjects
            // requires additional subscription setup, which is a follow-up enhancement.
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.Person?.FirstName != "John",
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see the replacement subject after client clears reference");

            Assert.NotEqual("John", server.Root.Person.FirstName);

            logger.Log("Test passed: Server sees client-cleared reference");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Server -> Client: Partial mutations (remove specific, replace value)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenServerRemovesSpecificCollectionItem_ThenClientRetainsOthers()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange: Start with 2 items
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            var serverContext = ((IInterceptorSubject)server.Root).Context;
            server.Root.People =
            [
                server.Root.People[0],
                new TestPerson(serverContext) { FirstName = "Bob", LastName = "Builder", Scores = [80.0] }
            ];

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 2,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial 2-item collection should sync");

            logger.Log($"Client has {client.Root.People.Length} people before removal");

            // Act: Remove first item, keep second
            server.Root.People = server.Root.People.Where(p => p.FirstName == "Bob").ToArray();
            logger.Log("Server removed first item, keeping Bob");

            // Assert: Client should have 1 item remaining (Bob) with values loaded
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1
                      && client.Root.People[0].FirstName == "Bob",
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see specific item removed while others remain, with values");

            Assert.Single(client.Root.People);
            Assert.Equal("Bob", client.Root.People[0].FirstName);

            logger.Log("Test passed: Client retained correct item after partial removal");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact(Skip = "Requires unique NodeIds: path-based NodeIds are reused when items are replaced at the same index")]
    public async Task WhenServerReplacesCollectionEntirely_ThenClientSeesNewItems()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial collection should sync");

            // Act: Replace entire collection with completely different items
            var serverContext = ((IInterceptorSubject)server.Root).Context;
            server.Root.People =
            [
                new TestPerson(serverContext) { FirstName = "X", LastName = "Alpha", Scores = [1.0] },
                new TestPerson(serverContext) { FirstName = "Y", LastName = "Beta", Scores = [2.0] },
                new TestPerson(serverContext) { FirstName = "Z", LastName = "Gamma", Scores = [3.0] }
            ];
            logger.Log("Server replaced entire collection with 3 new items");

            // Assert: wait for both structure AND values to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 3
                      && client.Root.People.Any(p => p.FirstName == "X"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see entirely new collection with values");

            Assert.Equal(3, client.Root.People.Length);
            Assert.Contains(client.Root.People, p => p.FirstName == "X");
            Assert.Contains(client.Root.People, p => p.FirstName == "Y");
            Assert.Contains(client.Root.People, p => p.FirstName == "Z");
            Assert.DoesNotContain(client.Root.People, p => p.FirstName == "Jane");

            logger.Log("Test passed: Client sees entirely replaced collection");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenServerRemovesSpecificDictionaryEntry_ThenClientRetainsOthers()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange: Start with 2 entries
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            var serverContext = ((IInterceptorSubject)server.Root).Context;
            server.Root.PeopleByName = new Dictionary<string, TestPerson>(server.Root.PeopleByName!)
            {
                ["bob"] = new TestPerson(serverContext) { FirstName = "Bob", LastName = "Builder", Scores = [80.0] }
            };

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.PeopleByName != null && client.Root.PeopleByName.Count == 2,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial 2-entry dictionary should sync");

            logger.Log($"Client has {client.Root.PeopleByName!.Count} entries before removal");

            // Act: Remove "alice", keep "bob"
            server.Root.PeopleByName = server.Root.PeopleByName
                .Where(kvp => kvp.Key != "alice")
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            logger.Log("Server removed alice, keeping bob");

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.PeopleByName != null && client.Root.PeopleByName.Count == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see specific entry removed while others remain");

            Assert.Single(client.Root.PeopleByName!);
            Assert.True(client.Root.PeopleByName.ContainsKey("bob"));
            Assert.False(client.Root.PeopleByName.ContainsKey("alice"));

            logger.Log("Test passed: Client retained correct entry after partial removal");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact(Skip = "Requires unique NodeIds: path-based NodeIds are reused when values are replaced at the same key")]
    public async Task WhenServerReplacesValueAtExistingDictionaryKey_ThenClientSeesNewSubject()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.PeopleByName != null && client.Root.PeopleByName.Count == 1
                      && client.Root.PeopleByName.ContainsKey("alice"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial dictionary with alice should sync");

            Assert.Equal("Alice", client.Root.PeopleByName!["alice"].FirstName);
            logger.Log("Initial alice entry synced");

            // Act: Replace subject at existing key "alice" with a different subject
            var serverContext = ((IInterceptorSubject)server.Root).Context;
            server.Root.PeopleByName = new Dictionary<string, TestPerson>
            {
                ["alice"] = new TestPerson(serverContext) { FirstName = "Alicia", LastName = "Replaced", Scores = [99.0] }
            };
            logger.Log("Server replaced alice with Alicia at same key");

            // Assert: Client should see the new subject at the same key (wait for values too)
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.PeopleByName != null
                      && client.Root.PeopleByName.ContainsKey("alice")
                      && client.Root.PeopleByName["alice"].FirstName == "Alicia",
                timeout: TimeSpan.FromSeconds(60),
                message: "Client should see replaced subject at existing dictionary key with new values");

            Assert.Equal("Alicia", client.Root.PeopleByName!["alice"].FirstName);
            Assert.Equal("Replaced", client.Root.PeopleByName["alice"].LastName);

            logger.Log("Test passed: Client sees replaced subject at existing key");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Loop prevention
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenServerMutates_ThenNoInfiniteLoop()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            // Wait for initial sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync");

            logger.Log("Initial sync verified, starting loop prevention test");

            // Act: Server adds a collection item.
            // If loop prevention is broken, this will cause infinite add/remove cycles
            // and the test will timeout or crash.
            server.Root.People =
            [
                ..server.Root.People,
                new TestPerson(((IInterceptorSubject)server.Root).Context) { FirstName = "Loop", LastName = "Test", Scores = [1.0] }
            ];

            // Wait for the structural change to propagate to the client
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 2,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see the added item without infinite looping");

            // Assert: Both sides should have exactly 2 items, not growing indefinitely.
            // Wait a bit more to ensure no runaway propagation occurs.
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.Equal(2, server.Root.People.Length);
            Assert.Equal(2, client.Root.People.Length);

            logger.Log($"Server People count: {server.Root.People.Length}");
            logger.Log($"Client People count: {client.Root.People.Length}");
            logger.Log("Test passed: No infinite loop detected");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }
}

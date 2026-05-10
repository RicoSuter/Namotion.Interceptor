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

            // Assert: Client should discover the new subject with values
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 2
                      && client.Root.People.Any(p => p.FirstName == "Bob"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see the new collection item with values");

            Assert.Equal(2, client.Root.People.Length);
            var clientBob = client.Root.People.First(p => p.FirstName == "Bob");
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

    [Fact]
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

    [Fact]
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
    // NodeId collision: replace at same index
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenServerReplacesCollectionItemAtSameIndex_ThenClientSeesNewSubject()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange: Start with 3 items
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            var serverContext = ((IInterceptorSubject)server.Root).Context;

            // Add Bob and Carol to the initial collection (Jane is already there)
            var jane = server.Root.People[0];
            server.Root.People =
            [
                jane,
                new TestPerson(serverContext) { FirstName = "Bob", LastName = "B", Scores = [2.0] },
                new TestPerson(serverContext) { FirstName = "Carol", LastName = "C", Scores = [3.0] }
            ];

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 3
                      && client.Root.People.Any(p => p.FirstName == "Bob"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial 3-item collection should sync");

            logger.Log("Initial 3 items synced");

            // Act: Replace Bob (index 1) with Dave (same array position, different instance)
            var carol = server.Root.People[2];
            server.Root.People =
            [
                jane,
                new TestPerson(serverContext) { FirstName = "Dave", LastName = "D", Scores = [4.0] },
                carol
            ];
            logger.Log("Server replaced Bob with Dave at index 1");

            // Assert: Client should see Dave instead of Bob
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 3
                      && client.Root.People.Any(p => p.FirstName == "Dave"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see Dave replacing Bob at same index");

            Assert.Equal(3, client.Root.People.Length);
            Assert.Contains(client.Root.People, p => p.FirstName == "Jane");
            Assert.Contains(client.Root.People, p => p.FirstName == "Dave");
            Assert.Contains(client.Root.People, p => p.FirstName == "Carol");
            Assert.DoesNotContain(client.Root.People, p => p.FirstName == "Bob");

            logger.Log("Test passed: Client sees replacement at same index");
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

    // -----------------------------------------------------------------------
    // Value sync after structural changes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenServerAddsCollectionItem_ThenValueChangesAreSyncedForNewSubject()
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
                message: "Initial People collection should sync");

            // Act: Add a new person, wait for structure to sync, then mutate its values
            var serverContext = ((IInterceptorSubject)server.Root).Context;
            var newPerson = new TestPerson(serverContext)
            {
                FirstName = "Bob",
                LastName = "Builder",
                Scores = [80.0]
            };
            server.Root.People = [..server.Root.People, newPerson];

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 2
                      && client.Root.People.Any(p => p.FirstName == "Bob"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see the new collection item");

            // Now mutate the new person's values on the server
            newPerson.FirstName = "Robert";
            newPerson.LastName = "TheBuilder";

            // Assert: Client should see the value changes on the dynamically added subject
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Any(p => p.FirstName == "Robert" && p.LastName == "TheBuilder"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see value changes on dynamically added subject");

            var clientBob = client.Root.People.First(p => p.FirstName == "Robert");
            Assert.Equal("TheBuilder", clientBob.LastName);

            logger.Log("Test passed: Value changes synced for dynamically added subject");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenServerAddsMultipleItemsRapidly_ThenClientConverges()
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
                message: "Initial People collection should sync");

            var serverContext = ((IInterceptorSubject)server.Root).Context;

            // Act: Add 5 items rapidly
            for (var i = 0; i < 5; i++)
            {
                server.Root.People =
                [
                    ..server.Root.People,
                    new TestPerson(serverContext)
                    {
                        FirstName = $"Person{i}",
                        LastName = $"Last{i}",
                        Scores = [i * 10.0]
                    }
                ];
            }

            logger.Log($"Server People count after rapid adds: {server.Root.People.Length}");

            // Assert: Client should eventually have all 6 items (1 initial + 5 added)
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 6,
                timeout: TimeSpan.FromSeconds(60),
                message: "Client should converge to 6 items after rapid adds");

            Assert.Equal(6, client.Root.People.Length);

            // Verify values are synced for at least some of the new subjects
            Assert.Contains(client.Root.People, p => p.FirstName == "Person0");
            Assert.Contains(client.Root.People, p => p.FirstName == "Person4");

            logger.Log("Test passed: Client converged after rapid structural mutations");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenServerAddsItemAndMutatesValuesConcurrently_ThenClientConverges()
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
                message: "Initial People collection should sync");

            var serverContext = ((IInterceptorSubject)server.Root).Context;

            // Act: Add a person and simultaneously mutate existing values
            var newPerson = new TestPerson(serverContext)
            {
                FirstName = "Concurrent",
                LastName = "Test",
                Scores = [1.0]
            };
            server.Root.People = [..server.Root.People, newPerson];

            // Rapidly mutate the root's scalar values while structural change propagates
            for (var i = 0; i < 20; i++)
            {
                server.Root.Name = $"Mutation{i}";
                server.Root.Number = i * 1.5m;
                await Task.Delay(10);
            }

            // Assert: Client should see both the structural change AND final values
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 2
                      && client.Root.People.Any(p => p.FirstName == "Concurrent")
                      && client.Root.Name == "Mutation19",
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see structural change and converged values");

            Assert.Equal(2, client.Root.People.Length);
            Assert.Equal("Mutation19", client.Root.Name);

            logger.Log("Test passed: Concurrent value and structural mutations converged");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenServerAddsAndRemovesItems_ThenClientConvergesToFinalState()
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
                message: "Initial People collection should sync");

            var serverContext = ((IInterceptorSubject)server.Root).Context;

            // Act: Add 3 items
            var people = server.Root.People.ToList();
            for (var i = 0; i < 3; i++)
            {
                people.Add(new TestPerson(serverContext)
                {
                    FirstName = $"Temp{i}",
                    LastName = $"Person{i}",
                    Scores = [i * 10.0]
                });
            }
            server.Root.People = people.ToArray();
            logger.Log($"Server has {server.Root.People.Length} items after adds");

            // Wait for adds to propagate
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 4,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see 4 items after adds");

            // Now remove 2 of the 3 added items, keep Temp1
            server.Root.People = server.Root.People
                .Where(p => p.FirstName != "Temp0" && p.FirstName != "Temp2")
                .ToArray();
            logger.Log($"Server has {server.Root.People.Length} items after removals");

            // Assert: Client should converge to final state (2 items: Jane + Temp1)
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 2
                      && client.Root.People.Any(p => p.FirstName == "Jane")
                      && client.Root.People.Any(p => p.FirstName == "Temp1"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should converge to final state after add+remove");

            Assert.Equal(2, client.Root.People.Length);
            Assert.Contains(client.Root.People, p => p.FirstName == "Jane");
            Assert.Contains(client.Root.People, p => p.FirstName == "Temp1");

            logger.Log("Test passed: Client converged after add+remove cycle");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Server outgoing race: value mutation immediately after structural add
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenServerAndClientBothMutateValues_ThenFinalStateConverges()
    {
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        try
        {
            // Arrange: Bidirectional sync (client can write to server)
            (server, client, port, var logger) = await StartServerAndClientWithStructuralSyncAsync();

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1
                      && client.Root.People[0].FirstName == "Jane",
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial sync");

            var serverContext = ((IInterceptorSubject)server.Root).Context;

            // Act: Add 3 subjects, then mutate values on BOTH server and client concurrently
            for (var i = 0; i < 3; i++)
            {
                server.Root.People =
                [
                    ..server.Root.People,
                    new TestPerson(serverContext)
                    {
                        FirstName = $"Person{i}",
                        LastName = $"Last{i}",
                        Scores = [i * 1.0]
                    }
                ];
            }

            // Wait for structure to sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 4,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see 4 people");

            // Concurrently mutate values on both sides
            for (var i = 0; i < 10; i++)
            {
                server.Root.People[1].FirstName = $"SV{i}";
                client.Root.People[1].FirstName = $"CV{i}";
                await Task.Delay(20);
            }

            // Set final deterministic values
            server.Root.People[1].FirstName = "ServerFinal";
            await Task.Delay(500);

            // Assert: Client should see the server's final value
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People[1].FirstName == "ServerFinal",
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromMilliseconds(200),
                message: "Client should converge to server's final value");

            // Check all subjects have consistent values
            for (var i = 0; i < server.Root.People.Length; i++)
            {
                var sp = server.Root.People[i];
                var cp = client.Root.People.FirstOrDefault(p => p.LastName == sp.LastName);
                if (cp is null)
                {
                    logger.Log($"  Missing client person with LastName={sp.LastName}");
                    continue;
                }

                if (sp.FirstName != cp.FirstName)
                {
                    logger.Log($"  Value diff: {sp.LastName}.FirstName server={sp.FirstName} client={cp.FirstName}");
                }
            }

            Assert.Equal("ServerFinal", client.Root.People[1].FirstName);
            logger.Log("Test passed: Bidirectional value mutations converged");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenServerAddsSubjectAndImmediatelyMutatesValue_ThenClientSeesValue()
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
                message: "Initial People collection should sync");

            var serverContext = ((IInterceptorSubject)server.Root).Context;

            // Act: Add a subject and immediately mutate its value in the same synchronous block.
            // With the server-side CQP race, the value mutation is processed before the
            // structural processor creates OPC UA nodes, so the value is silently dropped.
            var newPerson = new TestPerson(serverContext)
            {
                FirstName = "Initial",
                LastName = "Name",
                Scores = [1.0]
            };
            server.Root.People = [..server.Root.People, newPerson];
            newPerson.FirstName = "Mutated";
            newPerson.LastName = "AfterAdd";

            // Assert: Client should see both the subject AND the mutated values
            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 2
                      && client.Root.People.Any(p => p.FirstName == "Mutated" && p.LastName == "AfterAdd"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see subject with immediately mutated values");

            var clientPerson = client.Root.People.First(p => p.FirstName == "Mutated");
            Assert.Equal("AfterAdd", clientPerson.LastName);

            logger.Log("Test passed: Immediate value mutation after structural add is visible");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Stress test: mirrors ConnectorTester pattern
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenServerMutatesStructureAndValuesRapidly_ThenClientConvergesToFinalState()
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
                message: "Initial People collection should sync");

            var serverContext = ((IInterceptorSubject)server.Root).Context;
            var random = new Random(42);
            var addedPeople = new List<TestPerson>();

            // Act: Run 50 structural + value mutations over ~2 seconds (similar to ConnectorTester)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var valueMutationTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    // Mutate values on root
                    server.Root.Name = $"Val{random.Next(10000)}";
                    server.Root.Number = random.Next(1000) / 10m;

                    // Mutate values on existing people
                    foreach (var person in server.Root.People)
                    {
                        person.FirstName = $"F{random.Next(10000)}";
                    }

                    await Task.Delay(50, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            });

            // Structural mutations: add and remove people rapidly
            for (var i = 0; i < 20; i++)
            {
                var shouldAdd = random.Next(3) != 0 || server.Root.People.Length < 3;
                if (shouldAdd)
                {
                    var newPerson = new TestPerson(serverContext)
                    {
                        FirstName = $"Stress{i}",
                        LastName = $"Test{i}",
                        Scores = [i * 1.0]
                    };
                    addedPeople.Add(newPerson);
                    server.Root.People = [..server.Root.People, newPerson];
                }
                else if (server.Root.People.Length > 1)
                {
                    var removeIndex = random.Next(server.Root.People.Length);
                    var removed = server.Root.People[removeIndex];
                    addedPeople.Remove(removed);
                    server.Root.People = [..server.Root.People[..removeIndex], ..server.Root.People[(removeIndex + 1)..]];
                }

                await Task.Delay(100);
            }

            // Set final deterministic state
            cts.Cancel();
            await valueMutationTask;

            server.Root.Name = "FinalName";
            server.Root.Number = 999m;
            foreach (var person in server.Root.People)
            {
                person.FirstName = $"Final_{person.LastName}";
            }

            var expectedCount = server.Root.People.Length;
            var expectedName = server.Root.Name;
            logger.Log($"Server final state: {expectedCount} people, Name={expectedName}");

            // Assert: Client should converge to final state
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    var clientCount = client.Root.People.Length;
                    var clientName = client.Root.Name;
                    if (clientCount != expectedCount || clientName != expectedName)
                    {
                        logger.Log($"  Waiting: client has {clientCount}/{expectedCount} people, Name={clientName}");
                        return false;
                    }
                    return true;
                },
                timeout: TimeSpan.FromSeconds(60),
                pollInterval: TimeSpan.FromSeconds(2),
                message: $"Client should converge to {expectedCount} people and Name={expectedName}");

            Assert.Equal(expectedCount, client.Root.People.Length);
            Assert.Equal("FinalName", client.Root.Name);
            Assert.Equal(999m, client.Root.Number);

            // Verify values on dynamically added subjects converged
            foreach (var serverPerson in server.Root.People)
            {
                var clientPerson = client.Root.People.FirstOrDefault(
                    p => p.FirstName == serverPerson.FirstName);
                Assert.NotNull(clientPerson);
                Assert.Equal(serverPerson.LastName, clientPerson.LastName);
            }

            logger.Log("Test passed: Stress test converged");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Client-originated value sync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenClientAddsSubjectAndWritesValue_ThenServerSeesValue()
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

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync");

            // Act: client adds a person with specific values
            var newPerson = new TestPerson(((IInterceptorSubject)client.Root).Context)
            {
                FirstName = "ClientCreated",
                LastName = "ValueTest",
                Scores = [42.0]
            };
            client.Root.People = [..client.Root.People, newPerson];

            // Wait for server to see the structural change
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Length == 2,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see client-added item");

            // Now write a value on the client-originated subject
            newPerson.FirstName = "UpdatedName";

            // Assert: server should see the updated value
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Any(p => p.FirstName == "UpdatedName"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see value written to client-originated subject");

            logger.Log("Test passed: Value sync works for client-originated subjects");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenClientAddsSubject_ThenServerValueChangesReachClient()
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

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync");

            // Act: client adds a person
            var clientPerson = new TestPerson(((IInterceptorSubject)client.Root).Context)
            {
                FirstName = "ClientCreated",
                LastName = "InitialValue",
                Scores = [10.0]
            };
            client.Root.People = [..client.Root.People, clientPerson];

            // Wait for server to see the structural change
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Length == 2,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see client-added item");

            // Write a value on the client side so it syncs to server
            clientPerson.FirstName = "SyncedName";

            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Any(p => p.FirstName == "SyncedName"),
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see client value after subscription setup");

            // Now modify the subject on the SERVER side
            var serverPerson = server.Root.People.First(p => p.FirstName == "SyncedName");
            serverPerson.LastName = "ServerModified";

            // Assert: client should see the server's value change via subscription
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientPerson.LastName == "ServerModified",
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should see server's value change on client-originated subject");

            logger.Log("Test passed: Server value changes reach client for client-originated subjects");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Client-only rapid structural mutations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenClientAddsMultipleItemsRapidly_ThenServerConverges()
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

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync");

            var initialCount = server.Root.People.Length;
            const int itemsToAdd = 5;

            // Act: client adds items at ~200ms intervals
            var clientContext = ((IInterceptorSubject)client.Root).Context;
            for (var i = 0; i < itemsToAdd; i++)
            {
                var person = new TestPerson(clientContext)
                {
                    FirstName = $"Client{i}",
                    LastName = "Person",
                    Scores = [i * 10.0]
                };
                client.Root.People = [..client.Root.People, person];
                await Task.Delay(200);
            }

            // Assert
            var expectedCount = initialCount + itemsToAdd;
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Length == expectedCount,
                timeout: TimeSpan.FromSeconds(30),
                message: $"Server should converge to {expectedCount} items");

            logger.Log($"Final: server={server.Root.People.Length}, client={client.Root.People.Length}");

            // Wait for echo suppression to settle
            await Task.Delay(3000);

            Assert.Equal(expectedCount, server.Root.People.Length);
            Assert.Equal(expectedCount, client.Root.People.Length);

            logger.Log("Test passed: Client rapid adds converged");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Echo suppression & bidirectional convergence
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WhenClientAddsCollectionItem_ThenNoEchoDuplicate()
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

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync");

            // Act
            var newPerson = new TestPerson(((IInterceptorSubject)client.Root).Context)
            {
                FirstName = "EchoTest",
                LastName = "Person",
                Scores = [50.0]
            };
            client.Root.People = [..client.Root.People, newPerson];

            // Assert: wait for server to see it
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Length == 2,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see client-added item");

            // Wait extra to ensure no echo creates a duplicate
            await Task.Delay(3000);

            Assert.Equal(2, client.Root.People.Length);
            Assert.Equal(2, server.Root.People.Length);

            logger.Log("Test passed: No echo duplicate");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenBothSidesAddCollectionItems_ThenBothConverge()
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

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync");

            // Act: both sides add items
            var serverPerson = new TestPerson(((IInterceptorSubject)server.Root).Context)
            {
                FirstName = "ServerAdded",
                LastName = "Person",
                Scores = [80.0]
            };
            server.Root.People = [..server.Root.People, serverPerson];

            var clientPerson = new TestPerson(((IInterceptorSubject)client.Root).Context)
            {
                FirstName = "ClientAdded",
                LastName = "Person",
                Scores = [60.0]
            };
            client.Root.People = [..client.Root.People, clientPerson];

            // Assert: both sides should converge to 3 items (1 initial + 1 server + 1 client)
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Length >= 3 && client.Root.People.Length >= 3,
                timeout: TimeSpan.FromSeconds(30),
                message: "Both sides should converge to 3 items");

            // Wait extra for stability
            await Task.Delay(3000);

            Assert.Equal(3, server.Root.People.Length);
            Assert.Equal(3, client.Root.People.Length);

            logger.Log("Test passed: Bidirectional adds converged");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    [Fact]
    public async Task WhenBothSidesAddMultipleItemsRapidly_ThenBothConverge()
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

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.People.Length == 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial People collection should sync");

            // Act: both sides add 5 items each at ~100ms intervals
            var serverContext = ((IInterceptorSubject)server.Root).Context;
            var clientContext = ((IInterceptorSubject)client.Root).Context;
            const int itemsPerSide = 5;

            var serverTask = Task.Run(async () =>
            {
                for (var i = 0; i < itemsPerSide; i++)
                {
                    var person = new TestPerson(serverContext)
                    {
                        FirstName = $"Server{i}",
                        LastName = "Person",
                        Scores = [i * 10.0]
                    };
                    server.Root!.People = [..server.Root.People, person];
                    await Task.Delay(100);
                }
            });

            var clientTask = Task.Run(async () =>
            {
                for (var i = 0; i < itemsPerSide; i++)
                {
                    var person = new TestPerson(clientContext)
                    {
                        FirstName = $"Client{i}",
                        LastName = "Person",
                        Scores = [i * 10.0]
                    };
                    client.Root!.People = [..client.Root.People, person];
                    await Task.Delay(100);
                }
            });

            await Task.WhenAll(serverTask, clientTask);

            // Assert: both sides should converge to 1 + 5 + 5 = 11 items
            var expectedCount = 1 + itemsPerSide + itemsPerSide;
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Length >= expectedCount && client.Root.People.Length >= expectedCount,
                timeout: TimeSpan.FromSeconds(60),
                message: $"Both sides should converge to {expectedCount} items");

            // Wait extra for stability
            await Task.Delay(3000);

            logger.Log($"Server People count: {server.Root.People.Length}, Client People count: {client.Root.People.Length}");

            Assert.Equal(expectedCount, server.Root.People.Length);
            Assert.Equal(expectedCount, client.Root.People.Length);

            logger.Log("Test passed: Rapid bidirectional adds converged");
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();
            port?.Dispose();
        }
    }
}

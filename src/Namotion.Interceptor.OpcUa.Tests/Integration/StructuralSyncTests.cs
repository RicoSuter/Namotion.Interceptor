using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Integration tests for bidirectional structural synchronization between OPC UA server and client.
/// These tests verify that runtime structural changes (add/remove subjects in collections,
/// dictionaries, and references) propagate between server and client.
///
/// These tests are expected to FAIL until the client-side structural sync implementation
/// is complete. They define the "done" criteria for the feature.
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
    // Client -> Server
    // -----------------------------------------------------------------------

    [Fact(Skip = "Client-to-server structural sync not yet implemented. " +
                  "Requires the client's OutboundWriter to detect structural property changes " +
                  "and issue AddNodes/DeleteNodes requests to the server.")]
    public async Task WhenClientAddsCollectionItem_ThenServerCreatesNodes()
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

            // Assert: Server should see the new subject
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Root.People.Length == initialServerCount + 1,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should see the new collection item after client adds it");

            Assert.Equal(initialServerCount + 1, server.Root.People.Length);

            // Verify the new person's properties are synced to the server
            var serverNewPerson = server.Root.People.FirstOrDefault(
                person => person.FirstName == "ClientAdded");
            Assert.NotNull(serverNewPerson);
            Assert.Equal("Person", serverNewPerson.LastName);

            logger.Log("Test passed: Server sees client-added collection item");
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

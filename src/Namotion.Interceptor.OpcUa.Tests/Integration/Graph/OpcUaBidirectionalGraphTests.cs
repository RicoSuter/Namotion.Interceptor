using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for bidirectional graph synchronization between server and client.
/// Tests that structural changes (add/remove subjects) propagate in both directions.
/// Uses dedicated server/client instances with EnableLiveSync enabled on both sides.
/// </summary>
public class OpcUaBidirectionalGraphTests : OpcUaGraphTestBase
{
    public OpcUaBidirectionalGraphTests(ITestOutputHelper output) : base(output)
    {
    }

    private async Task<GraphBidirectionalContext> StartBidirectionalWithExternalManagementAsync()
    {
        var typeRegistry = new OpcUaTypeRegistry();
        typeRegistry.RegisterType<TestPerson>(ObjectTypeIds.BaseObjectType);

        return await StartBidirectionalAsync(
            new GraphServerOptions
            {
                EnableLiveSync = true,
                EnableExternalNodeManagement = true,
                TypeRegistry = typeRegistry
            },
            new GraphClientOptions
            {
                EnableLiveSync = true,
                EnableRemoteNodeManagement = true,
                EnableModelChangeEvents = true,
                EnablePeriodicResync = false
            });
    }

    #region Server → Client: Collection Tests

    [Fact]
    public async Task ServerAddsToCollection_ClientModelUpdated()
    {
        await using var ctx = await StartBidirectionalAsync();

        var initialClientCount = ctx.ClientRoot.People.Length;
        Logger.Log($"Initial state: server.People.Length={ctx.ServerRoot.People.Length}, client.People.Length={initialClientCount}");

        // Act: Server adds a person to collection
        var newPerson = new TestPerson(ctx.ServerContext)
        {
            FirstName = "ServerAdded",
            LastName = "Person"
        };
        ctx.ServerRoot.People = [..ctx.ServerRoot.People, newPerson];
        Logger.Log($"Server added person: {newPerson.FirstName} {newPerson.LastName}");

        // Assert: Client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientCount = ctx.ClientRoot.People.Length;
                if (clientCount != initialClientCount + 1) return false;
                var lastPerson = ctx.ClientRoot.People.LastOrDefault();
                return lastPerson?.FirstName == "ServerAdded";
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive server's collection add with property values");

        Logger.Log($"After sync: client.People.Length={ctx.ClientRoot.People.Length}");
        Assert.Equal(initialClientCount + 1, ctx.ClientRoot.People.Length);

        var clientPerson = ctx.ClientRoot.People.LastOrDefault();
        Assert.NotNull(clientPerson);
        Assert.Equal("ServerAdded", clientPerson.FirstName);
        Assert.Equal("Person", clientPerson.LastName);
        Logger.Log("Server→Client collection add verified");
    }

    [Fact]
    public async Task ServerRemovesFromCollection_ClientModelUpdated()
    {
        await using var ctx = await StartBidirectionalAsync();

        // Setup: Add persons first
        var person1 = new TestPerson(ctx.ServerContext) { FirstName = "Keep", LastName = "This" };
        var person2 = new TestPerson(ctx.ServerContext) { FirstName = "Remove", LastName = "This" };
        ctx.ServerRoot.People = [person1, person2];
        Logger.Log("Server added two persons");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ClientRoot.People.Length == 2,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should sync initial two persons");

        Logger.Log($"Client synced initial state: {ctx.ClientRoot.People.Length} persons");

        // Act: Server removes one person
        ctx.ServerRoot.People = [person1];
        Logger.Log("Server removed second person");

        // Assert: Client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ClientRoot.People.Length == 1,
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive server's collection remove");

        Logger.Log($"After sync: client.People.Length={ctx.ClientRoot.People.Length}");
        Assert.Single(ctx.ClientRoot.People);
        Assert.Equal("Keep", ctx.ClientRoot.People[0].FirstName);
        Logger.Log("Server→Client collection remove verified");
    }

    #endregion

    #region Server → Client: Dictionary Tests

    [Fact]
    public async Task ServerAddsToDictionary_ClientModelUpdated()
    {
        await using var ctx = await StartBidirectionalAsync();

        Logger.Log($"Initial state: server.PeopleByName.Count={ctx.ServerRoot.PeopleByName?.Count ?? 0}");

        // Act: Server adds to dictionary
        var newPerson = new TestPerson(ctx.ServerContext)
        {
            FirstName = "DictServer",
            LastName = "Added"
        };
        ctx.ServerRoot.PeopleByName = new Dictionary<string, TestPerson>(ctx.ServerRoot.PeopleByName ?? new())
        {
            ["serverkey"] = newPerson
        };
        Logger.Log("Server added 'serverkey' to dictionary");

        // Assert: Client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ClientRoot.PeopleByName?.ContainsKey("serverkey") ?? false,
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive server's dictionary add");

        Logger.Log("After sync: client.PeopleByName contains 'serverkey'");
        Assert.True(ctx.ClientRoot.PeopleByName?.ContainsKey("serverkey"));
        Assert.Equal("DictServer", ctx.ClientRoot.PeopleByName!["serverkey"].FirstName);
        Logger.Log("Server→Client dictionary add verified");
    }

    [Fact]
    public async Task ServerRemovesFromDictionary_ClientModelUpdated()
    {
        await using var ctx = await StartBidirectionalAsync();

        // Setup: Add entries first
        var person1 = new TestPerson(ctx.ServerContext) { FirstName = "KeepDict", LastName = "Entry" };
        var person2 = new TestPerson(ctx.ServerContext) { FirstName = "RemoveDict", LastName = "Entry" };
        ctx.ServerRoot.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keep"] = person1,
            ["remove"] = person2
        };
        Logger.Log("Server added two dictionary entries");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => (ctx.ClientRoot.PeopleByName?.Count ?? 0) == 2,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should sync initial dictionary entries");

        Logger.Log($"Client synced initial state: {ctx.ClientRoot.PeopleByName?.Count} entries");

        // Act: Server removes one entry
        ctx.ServerRoot.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keep"] = person1
        };
        Logger.Log("Server removed 'remove' key");

        // Assert: Client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () => !(ctx.ClientRoot.PeopleByName?.ContainsKey("remove") ?? false),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive server's dictionary remove");

        Logger.Log("After sync: client.PeopleByName does not contain 'remove'");
        Assert.False(ctx.ClientRoot.PeopleByName?.ContainsKey("remove"));
        Assert.True(ctx.ClientRoot.PeopleByName?.ContainsKey("keep"));
        Logger.Log("Server→Client dictionary remove verified");
    }

    #endregion

    #region Server → Client: Reference Tests

    [Fact]
    public async Task ServerAssignsReference_ClientModelUpdated()
    {
        await using var ctx = await StartBidirectionalAsync();

        Logger.Log($"Initial state: server.Person={ctx.ServerRoot.Person?.FirstName ?? "null"}");

        // Act: Server assigns a Person reference
        var newPerson = new TestPerson(ctx.ServerContext)
        {
            FirstName = "RefServer",
            LastName = "Assigned"
        };
        ctx.ServerRoot.Person = newPerson;
        Logger.Log($"Server assigned Person: {newPerson.FirstName}");

        // Assert: Client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ClientRoot.Person?.FirstName == "RefServer",
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive server's reference assignment");

        Logger.Log($"After sync: client.Person.FirstName={ctx.ClientRoot.Person?.FirstName}");
        Assert.Equal("RefServer", ctx.ClientRoot.Person?.FirstName);
        Assert.Equal("Assigned", ctx.ClientRoot.Person?.LastName);
        Logger.Log("Server→Client reference assignment verified");
    }

    [Fact]
    public async Task ServerClearsReference_ClientModelUpdated()
    {
        await using var ctx = await StartBidirectionalAsync();

        // Setup: Assign a Person first
        var person = new TestPerson(ctx.ServerContext) { FirstName = "ToClear", LastName = "Ref" };
        ctx.ServerRoot.Person = person;
        Logger.Log("Server assigned initial Person");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ClientRoot.Person?.FirstName == "ToClear",
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should sync initial Person");

        Logger.Log($"Client synced initial Person: {ctx.ClientRoot.Person?.FirstName}");

        // Act: Server clears the reference
        ctx.ServerRoot.Person = null!;
        Logger.Log("Server cleared Person reference");

        // Assert: Client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ClientRoot.Person == null,
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive server's reference clear");

        Logger.Log("After sync: client.Person is null");
        Assert.Null(ctx.ClientRoot.Person);
        Logger.Log("Server→Client reference clear verified");
    }

    #endregion

    #region Client → Server: Collection Tests

    [Fact]
    public async Task ClientAddsToCollection_ServerModelUpdated()
    {
        await using var ctx = await StartBidirectionalWithExternalManagementAsync();

        var initialServerCount = ctx.ServerRoot.People.Length;
        Logger.Log($"Initial state: server.People.Length={initialServerCount}, client.People.Length={ctx.ClientRoot.People.Length}");

        // Act: Client adds a person to collection
        var newPerson = new TestPerson(ctx.ClientContext)
        {
            FirstName = "ClientAdded",
            LastName = "Person"
        };
        ctx.ClientRoot.People = [..ctx.ClientRoot.People, newPerson];
        Logger.Log($"Client added person: {newPerson.FirstName} {newPerson.LastName}");

        // Assert: Server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ServerRoot.People.Length == initialServerCount + 1,
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's collection add");

        Logger.Log($"After sync: server.People.Length={ctx.ServerRoot.People.Length}");
        Assert.Equal(initialServerCount + 1, ctx.ServerRoot.People.Length);

        var serverPerson = ctx.ServerRoot.People.LastOrDefault();
        Assert.NotNull(serverPerson);
        Assert.Equal("ClientAdded", serverPerson.FirstName);
        Logger.Log("Client→Server collection add verified");
    }

    [Fact]
    public async Task ClientRemovesFromCollection_ServerModelUpdated()
    {
        await using var ctx = await StartBidirectionalWithExternalManagementAsync();

        // Setup: Server starts with persons, client syncs them
        var person1 = new TestPerson(ctx.ServerContext) { FirstName = "KeepClient", LastName = "Test" };
        var person2 = new TestPerson(ctx.ServerContext) { FirstName = "RemoveClient", LastName = "Test" };
        ctx.ServerRoot.People = [person1, person2];
        Logger.Log("Server initialized with two persons");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ClientRoot.People.Length == 2,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should sync initial two persons");

        Logger.Log($"Client synced: {ctx.ClientRoot.People.Length} persons");

        // Act: Client removes one person
        ctx.ClientRoot.People = [ctx.ClientRoot.People[0]];
        Logger.Log("Client removed second person");

        // Assert: Server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ServerRoot.People.Length == 1,
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's collection remove");

        Logger.Log($"After sync: server.People.Length={ctx.ServerRoot.People.Length}");
        Assert.Single(ctx.ServerRoot.People);
        Logger.Log("Client→Server collection remove verified");
    }

    #endregion

    #region Client → Server: Dictionary Tests

    [Fact]
    public async Task ClientAddsToDictionary_ServerModelUpdated()
    {
        await using var ctx = await StartBidirectionalWithExternalManagementAsync();

        Logger.Log($"Initial state: server.PeopleByName.Count={ctx.ServerRoot.PeopleByName?.Count ?? 0}");

        // Act: Client adds to dictionary
        var newPerson = new TestPerson(ctx.ClientContext)
        {
            FirstName = "DictClient",
            LastName = "Added"
        };
        ctx.ClientRoot.PeopleByName = new Dictionary<string, TestPerson>(ctx.ClientRoot.PeopleByName ?? new())
        {
            ["clientkey"] = newPerson
        };
        Logger.Log("Client added 'clientkey' to dictionary");

        // Assert: Server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ServerRoot.PeopleByName?.ContainsKey("clientkey") ?? false,
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's dictionary add");

        Logger.Log("After sync: server.PeopleByName contains 'clientkey'");
        Assert.True(ctx.ServerRoot.PeopleByName?.ContainsKey("clientkey"));
        Assert.Equal("DictClient", ctx.ServerRoot.PeopleByName!["clientkey"].FirstName);
        Logger.Log("Client→Server dictionary add verified");
    }

    [Fact]
    public async Task ClientRemovesFromDictionary_ServerModelUpdated()
    {
        await using var ctx = await StartBidirectionalWithExternalManagementAsync();

        // Setup: Server starts with dictionary entries
        var person1 = new TestPerson(ctx.ServerContext) { FirstName = "KeepDictC", LastName = "Entry" };
        var person2 = new TestPerson(ctx.ServerContext) { FirstName = "RemoveDictC", LastName = "Entry" };
        ctx.ServerRoot.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keepc"] = person1,
            ["removec"] = person2
        };
        Logger.Log("Server initialized with two dictionary entries");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => (ctx.ClientRoot.PeopleByName?.Count ?? 0) == 2,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should sync initial dictionary entries");

        Logger.Log($"Client synced: {ctx.ClientRoot.PeopleByName?.Count} entries");

        // Act: Client removes one entry
        var keepPerson = ctx.ClientRoot.PeopleByName!["keepc"];
        ctx.ClientRoot.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keepc"] = keepPerson
        };
        Logger.Log("Client removed 'removec' key");

        // Assert: Server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () => !(ctx.ServerRoot.PeopleByName?.ContainsKey("removec") ?? false),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's dictionary remove");

        Logger.Log("After sync: server.PeopleByName does not contain 'removec'");
        Assert.False(ctx.ServerRoot.PeopleByName?.ContainsKey("removec"));
        Assert.True(ctx.ServerRoot.PeopleByName?.ContainsKey("keepc"));
        Logger.Log("Client→Server dictionary remove verified");
    }

    #endregion

    #region Client → Server: Reference Tests

    [Fact]
    public async Task ClientAssignsReference_ServerModelUpdated()
    {
        await using var ctx = await StartBidirectionalWithExternalManagementAsync();

        Logger.Log($"Initial state: server.Person={ctx.ServerRoot.Person?.FirstName ?? "null"}");

        // Act: Client assigns a Person reference
        var newPerson = new TestPerson(ctx.ClientContext)
        {
            FirstName = "RefClient",
            LastName = "Assigned"
        };
        ctx.ClientRoot.Person = newPerson;
        Logger.Log($"Client assigned Person: {newPerson.FirstName}");

        // Assert: Server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () => ctx.ServerRoot.Person?.FirstName == "RefClient",
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's reference assignment");

        Logger.Log($"After sync: server.Person.FirstName={ctx.ServerRoot.Person?.FirstName}");
        Assert.Equal("RefClient", ctx.ServerRoot.Person?.FirstName);
        Logger.Log("Client→Server reference assignment verified");
    }

    #endregion
}

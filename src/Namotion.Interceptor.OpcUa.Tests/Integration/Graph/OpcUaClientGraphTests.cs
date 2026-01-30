using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for OPC UA client graph sync - verifies that local model changes create/remove MonitoredItems.
/// Tests the Client Model→OPC direction by verifying MonitoredItem counts after model changes.
/// Uses Theory tests to cover both event-based and periodic resync modes.
/// </summary>
public class OpcUaClientGraphTests : OpcUaGraphTestBase
{
    public OpcUaClientGraphTests(ITestOutputHelper output) : base(output)
    {
    }

    #region Collection Tests

    [Theory]
    [InlineData(true, false, "EventBased")]
    [InlineData(false, true, "PeriodicResync")]
    public async Task AddSubjectToCollection_MonitoredItemsCreated(
        bool enableModelChangeEvents, bool enablePeriodicResync, string syncMode)
    {
        await using var serverCtx = await StartServerAsync();

        var clientOptions = new GraphClientOptions
        {
            EnableModelChangeEvents = enableModelChangeEvents,
            EnablePeriodicResync = enablePeriodicResync,
            PeriodicResyncInterval = TimeSpan.FromSeconds(1)
        };

        await using var clientCtx = await StartClientAsync(serverCtx.Port, clientOptions);

        Logger.Log($"Testing with sync mode: {syncMode}");

        var initialMonitoredItemCount = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"Initial MonitoredItemCount: {initialMonitoredItemCount}");

        // Verify client starts with empty People collection
        Assert.Empty(clientCtx.Root.People);

        // Act: Add a person to the SERVER's collection
        var serverPerson = new TestPerson(serverCtx.Context)
        {
            FirstName = "John",
            LastName = "Doe"
        };
        serverCtx.Root.People = [serverPerson];
        Logger.Log("Added person to server collection");

        // Wait for the client to receive the structural change
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientCtx.Root.People.Length == 1,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync the new person from server");

        // Verify the client has the person with correct properties
        Assert.Single(clientCtx.Root.People);
        var clientPerson = clientCtx.Root.People[0];

        await AsyncTestHelpers.WaitUntilAsync(
            () => clientPerson.FirstName == "John" && clientPerson.LastName == "Doe",
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync person properties");

        Assert.Equal("John", clientPerson.FirstName);
        Assert.Equal("Doe", clientPerson.LastName);
        Logger.Log($"Client synced person: {clientPerson.FirstName} {clientPerson.LastName}");

        // Verify MonitoredItemCount increased
        var finalMonitoredItemCount = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"Final MonitoredItemCount: {finalMonitoredItemCount}");

        Assert.True(finalMonitoredItemCount > initialMonitoredItemCount,
            $"Expected MonitoredItemCount to increase from {initialMonitoredItemCount}, but got {finalMonitoredItemCount}");

        Logger.Log("MonitoredItems created for added subject");
    }

    [Theory]
    [InlineData(true, false, "EventBased")]
    [InlineData(false, true, "PeriodicResync")]
    public async Task RemoveSubjectFromCollection_MonitoredItemsRemoved(
        bool enableModelChangeEvents, bool enablePeriodicResync, string syncMode)
    {
        await using var serverCtx = await StartServerAsync();

        var clientOptions = new GraphClientOptions
        {
            EnableModelChangeEvents = enableModelChangeEvents,
            EnablePeriodicResync = enablePeriodicResync,
            PeriodicResyncInterval = TimeSpan.FromSeconds(1)
        };

        await using var clientCtx = await StartClientAsync(serverCtx.Port, clientOptions);

        Logger.Log($"Testing with sync mode: {syncMode}");

        // Setup: Add two people to the collection
        var person1 = new TestPerson(serverCtx.Context) { FirstName = "Alice", LastName = "Smith" };
        var person2 = new TestPerson(serverCtx.Context) { FirstName = "Bob", LastName = "Jones" };
        serverCtx.Root.People = [person1, person2];
        Logger.Log("Added two people to server collection");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientCtx.Root.People.Length == 2,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync two people from server");

        var monitoredItemCountWithTwo = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"MonitoredItemCount with two people: {monitoredItemCountWithTwo}");

        // Act: Remove one person from the server's collection
        serverCtx.Root.People = [person1];
        Logger.Log("Removed Bob from server collection");

        // Wait for the client to receive the structural change
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientCtx.Root.People.Length == 1,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync removal from server");

        Assert.Single(clientCtx.Root.People);
        Logger.Log($"Client now has {clientCtx.Root.People.Length} person(s)");

        // Allow time for cleanup
        await Task.Delay(500);

        var finalMonitoredItemCount = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"Final MonitoredItemCount: {finalMonitoredItemCount}");

        Assert.True(finalMonitoredItemCount <= monitoredItemCountWithTwo,
            $"Expected MonitoredItemCount to decrease from {monitoredItemCountWithTwo}, but got {finalMonitoredItemCount}");

        Logger.Log("MonitoredItems cleaned up for removed subject");
    }

    #endregion

    #region Reference Tests

    [Theory]
    [InlineData(true, false, "EventBased")]
    [InlineData(false, true, "PeriodicResync")]
    public async Task AssignSubjectReference_MonitoredItemsCreated(
        bool enableModelChangeEvents, bool enablePeriodicResync, string syncMode)
    {
        await using var serverCtx = await StartServerAsync();

        var clientOptions = new GraphClientOptions
        {
            EnableModelChangeEvents = enableModelChangeEvents,
            EnablePeriodicResync = enablePeriodicResync,
            PeriodicResyncInterval = TimeSpan.FromSeconds(1)
        };

        await using var clientCtx = await StartClientAsync(serverCtx.Port, clientOptions);

        Logger.Log($"Testing with sync mode: {syncMode}");

        var initialMonitoredItemCount = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"Initial MonitoredItemCount: {initialMonitoredItemCount}");

        // Verify client starts with null Person reference
        Assert.Null(clientCtx.Root.Person);

        // Act: Assign a person reference on the server
        var serverPerson = new TestPerson(serverCtx.Context)
        {
            FirstName = "RefTest",
            LastName = "Person"
        };
        serverCtx.Root.Person = serverPerson;
        Logger.Log("Assigned Person reference on server");

        // Wait for the client to receive the structural change
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientCtx.Root.Person != null,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync Person reference from server");

        await AsyncTestHelpers.WaitUntilAsync(
            () => clientCtx.Root.Person?.FirstName == "RefTest",
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync person properties");

        Assert.NotNull(clientCtx.Root.Person);
        Assert.Equal("RefTest", clientCtx.Root.Person.FirstName);
        Logger.Log($"Client synced Person: {clientCtx.Root.Person.FirstName}");

        // Verify MonitoredItemCount increased
        var finalMonitoredItemCount = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"Final MonitoredItemCount: {finalMonitoredItemCount}");

        Assert.True(finalMonitoredItemCount > initialMonitoredItemCount,
            $"Expected MonitoredItemCount to increase from {initialMonitoredItemCount}, but got {finalMonitoredItemCount}");

        Logger.Log("MonitoredItems created for assigned reference");
    }

    [Theory]
    [InlineData(true, false, "EventBased")]
    [InlineData(false, true, "PeriodicResync")]
    public async Task ClearSubjectReference_MonitoredItemsRemoved(
        bool enableModelChangeEvents, bool enablePeriodicResync, string syncMode)
    {
        await using var serverCtx = await StartServerAsync();

        // Setup: Assign initial person reference on server before client connects
        var serverPerson = new TestPerson(serverCtx.Context)
        {
            FirstName = "ToClear",
            LastName = "Person"
        };
        serverCtx.Root.Person = serverPerson;
        Logger.Log("Assigned initial Person reference on server");

        var clientOptions = new GraphClientOptions
        {
            EnableModelChangeEvents = enableModelChangeEvents,
            EnablePeriodicResync = enablePeriodicResync,
            PeriodicResyncInterval = TimeSpan.FromSeconds(1)
        };

        await using var clientCtx = await StartClientAsync(serverCtx.Port, clientOptions);

        Logger.Log($"Testing with sync mode: {syncMode}");

        // Wait for client to sync the Person reference
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientCtx.Root.Person?.FirstName == "ToClear",
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync initial Person reference");

        Logger.Log($"Client synced initial Person: {clientCtx.Root.Person?.FirstName}");

        var monitoredItemCountWithPerson = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"MonitoredItemCount with Person: {monitoredItemCountWithPerson}");

        // Act: Clear the reference on server
        serverCtx.Root.Person = null!;
        Logger.Log("Cleared Person reference on server");

        // Wait for the client to receive the structural change
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientCtx.Root.Person == null,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync reference clear from server");

        Assert.Null(clientCtx.Root.Person);
        Logger.Log("Client Person reference is now null");

        // Allow time for cleanup
        await Task.Delay(500);

        var finalMonitoredItemCount = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"Final MonitoredItemCount: {finalMonitoredItemCount}");

        Assert.True(finalMonitoredItemCount <= monitoredItemCountWithPerson,
            $"Expected MonitoredItemCount to decrease from {monitoredItemCountWithPerson}, but got {finalMonitoredItemCount}");

        Logger.Log("MonitoredItems cleaned up for cleared reference");
    }

    #endregion

    #region Dictionary Tests

    [Theory]
    [InlineData(true, false, "EventBased")]
    [InlineData(false, true, "PeriodicResync")]
    public async Task AddToDictionary_MonitoredItemsCreated(
        bool enableModelChangeEvents, bool enablePeriodicResync, string syncMode)
    {
        await using var serverCtx = await StartServerAsync();

        var clientOptions = new GraphClientOptions
        {
            EnableModelChangeEvents = enableModelChangeEvents,
            EnablePeriodicResync = enablePeriodicResync,
            PeriodicResyncInterval = TimeSpan.FromSeconds(1)
        };

        await using var clientCtx = await StartClientAsync(serverCtx.Port, clientOptions);

        Logger.Log($"Testing with sync mode: {syncMode}");

        var initialMonitoredItemCount = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"Initial MonitoredItemCount: {initialMonitoredItemCount}");

        // Verify client starts with empty dictionary
        Assert.Empty(clientCtx.Root.PeopleByName ?? new Dictionary<string, TestPerson>());

        // Act: Add to dictionary on server
        var serverPerson = new TestPerson(serverCtx.Context)
        {
            FirstName = "DictTest",
            LastName = "Person"
        };
        serverCtx.Root.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["testkey"] = serverPerson
        };
        Logger.Log("Added 'testkey' to server dictionary");

        // Wait for the client to receive the structural change
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientCtx.Root.PeopleByName?.ContainsKey("testkey") ?? false,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync dictionary entry from server");

        Assert.True(clientCtx.Root.PeopleByName?.ContainsKey("testkey"));

        await AsyncTestHelpers.WaitUntilAsync(
            () => clientCtx.Root.PeopleByName?["testkey"].FirstName == "DictTest",
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync dictionary entry properties");

        Assert.Equal("DictTest", clientCtx.Root.PeopleByName!["testkey"].FirstName);
        Logger.Log($"Client synced dictionary entry: {clientCtx.Root.PeopleByName["testkey"].FirstName}");

        // Verify MonitoredItemCount increased
        var finalMonitoredItemCount = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"Final MonitoredItemCount: {finalMonitoredItemCount}");

        Assert.True(finalMonitoredItemCount > initialMonitoredItemCount,
            $"Expected MonitoredItemCount to increase from {initialMonitoredItemCount}, but got {finalMonitoredItemCount}");

        Logger.Log("MonitoredItems created for dictionary entry");
    }

    [Theory]
    [InlineData(true, false, "EventBased")]
    [InlineData(false, true, "PeriodicResync")]
    public async Task RemoveFromDictionary_MonitoredItemsRemoved(
        bool enableModelChangeEvents, bool enablePeriodicResync, string syncMode)
    {
        await using var serverCtx = await StartServerAsync();

        var clientOptions = new GraphClientOptions
        {
            EnableModelChangeEvents = enableModelChangeEvents,
            EnablePeriodicResync = enablePeriodicResync,
            PeriodicResyncInterval = TimeSpan.FromSeconds(1)
        };

        await using var clientCtx = await StartClientAsync(serverCtx.Port, clientOptions);

        Logger.Log($"Testing with sync mode: {syncMode}");

        // Setup: Add two entries to dictionary
        var person1 = new TestPerson(serverCtx.Context) { FirstName = "Keep", LastName = "Entry" };
        var person2 = new TestPerson(serverCtx.Context) { FirstName = "Remove", LastName = "Entry" };
        serverCtx.Root.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keep"] = person1,
            ["remove"] = person2
        };
        Logger.Log("Added two entries to server dictionary");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => (clientCtx.Root.PeopleByName?.Count ?? 0) == 2,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync two dictionary entries from server");

        var monitoredItemCountWithTwo = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"MonitoredItemCount with two entries: {monitoredItemCountWithTwo}");

        // Act: Remove one entry from server's dictionary
        serverCtx.Root.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keep"] = person1
        };
        Logger.Log("Removed 'remove' key from server dictionary");

        // Wait for the client to receive the structural change
        await AsyncTestHelpers.WaitUntilAsync(
            () => !(clientCtx.Root.PeopleByName?.ContainsKey("remove") ?? false),
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should sync removal from server");

        Assert.False(clientCtx.Root.PeopleByName?.ContainsKey("remove"));
        Assert.True(clientCtx.Root.PeopleByName?.ContainsKey("keep"));
        Logger.Log("Client dictionary no longer contains 'remove' key");

        // Allow time for cleanup
        await Task.Delay(500);

        var finalMonitoredItemCount = clientCtx.Diagnostics?.MonitoredItemCount ?? 0;
        Logger.Log($"Final MonitoredItemCount: {finalMonitoredItemCount}");

        Assert.True(finalMonitoredItemCount <= monitoredItemCountWithTwo,
            $"Expected MonitoredItemCount to decrease from {monitoredItemCountWithTwo}, but got {finalMonitoredItemCount}");

        Logger.Log("MonitoredItems cleaned up for removed dictionary entry");
    }

    #endregion
}

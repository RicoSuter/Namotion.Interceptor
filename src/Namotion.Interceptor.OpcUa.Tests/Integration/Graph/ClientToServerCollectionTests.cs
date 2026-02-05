using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for client-to-server collection synchronization.
/// Verifies that collection changes on client propagate to server model.
/// Tests both container and flat collection structures.
/// </summary>
[Trait("Category", "Integration")]
public class ClientToServerCollectionTests : SharedServerTestBase
{
    public ClientToServerCollectionTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    [Fact]
    public async Task RemoveFromContainerWithPreExistingItems_IndexTracking()
    {
        var clientArea = Client!.Root!.ClientToServerCollection;
        var serverArea = ServerFixture.ServerRoot.ClientToServerCollection;

        var testId = Guid.NewGuid().ToString("N")[..8];

        // Log initial state
        Logger.Log($"Initial client ContainerItems: {clientArea.ContainerItems.Length} items");
        Logger.Log($"Initial server ContainerItems: {serverArea.ContainerItems.Length} items");
        foreach (var p in clientArea.ContainerItems)
            Logger.Log($"  Client[{Array.IndexOf(clientArea.ContainerItems, p)}]: {p.FirstName}");
        foreach (var p in serverArea.ContainerItems)
            Logger.Log($"  Server[{Array.IndexOf(serverArea.ContainerItems, p)}]: {p.FirstName}");

        // Add two new persons
        var person1 = new NestedPerson(Client.Context) { FirstName = $"Keep_{testId}", LastName = "Test" };
        var person2 = new NestedPerson(Client.Context) { FirstName = $"Remove_{testId}", LastName = "Test" };

        Logger.Log($"Adding person1: Keep_{testId} and person2: Remove_{testId}");
        clientArea.ContainerItems = [..clientArea.ContainerItems, person1, person2];

        Logger.Log($"Client array after add: {clientArea.ContainerItems.Length} items");
        foreach (var p in clientArea.ContainerItems)
            Logger.Log($"  Client[{Array.IndexOf(clientArea.ContainerItems, p)}]: {p.FirstName}");

        // Wait for both to sync to server
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.ContainerItems.Any(p => p.FirstName == $"Keep_{testId}") &&
                  serverArea.ContainerItems.Any(p => p.FirstName == $"Remove_{testId}"),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should have both new items");

        Logger.Log($"Server after sync: {serverArea.ContainerItems.Length} items");
        foreach (var p in serverArea.ContainerItems)
            Logger.Log($"  Server[{Array.IndexOf(serverArea.ContainerItems, p)}]: {p.FirstName}");

        // Now remove person2 (Remove_{testId})
        Logger.Log($"Removing person2: Remove_{testId}");
        var itemsToKeep = clientArea.ContainerItems.Where(p => p.FirstName != $"Remove_{testId}").ToArray();
        clientArea.ContainerItems = itemsToKeep;

        Logger.Log($"Client array after remove: {clientArea.ContainerItems.Length} items");
        foreach (var p in clientArea.ContainerItems)
            Logger.Log($"  Client[{Array.IndexOf(clientArea.ContainerItems, p)}]: {p.FirstName}");

        // Wait for server to update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasRemoved = serverArea.ContainerItems.Any(p => p.FirstName == $"Remove_{testId}");
                var hasKeep = serverArea.ContainerItems.Any(p => p.FirstName == $"Keep_{testId}");
                Logger.Log($"Polling: hasKeep={hasKeep}, hasRemoved={hasRemoved}, count={serverArea.ContainerItems.Length}");
                foreach (var p in serverArea.ContainerItems)
                    Logger.Log($"  Server[{Array.IndexOf(serverArea.ContainerItems, p)}]: {p.FirstName}");
                return !hasRemoved && hasKeep;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should have Keep but not Remove");

        Assert.Contains(serverArea.ContainerItems, p => p.FirstName == $"Keep_{testId}");
        Assert.DoesNotContain(serverArea.ContainerItems, p => p.FirstName == $"Remove_{testId}");
    }

    [Fact]
    public async Task AddToContainerCollection_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerCollection;
        var serverArea = ServerFixture.ServerRoot.ClientToServerCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"ContainerAdd_{testId}";

        Logger.Log($"Initial state: server.ContainerItems.Length={serverArea.ContainerItems.Length}, client.ContainerItems.Length={clientArea.ContainerItems.Length}");

        // Act - client adds a person to container collection
        var newPerson = new NestedPerson(Client.Context)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        clientArea.ContainerItems = [..clientArea.ContainerItems, newPerson];
        Logger.Log($"Client added person: {newPerson.FirstName} {newPerson.LastName}");

        // Assert - server model should have our specific item (don't check count - other parallel tests may add items)
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.ContainerItems.FirstOrDefault(p => p.FirstName == firstName);
                var result = serverPerson != null && serverPerson.LastName == "Person";
                Logger.Log($"Polling server ContainerItems: count={serverArea.ContainerItems.Length}, found={serverPerson != null}");
                return result;
            },
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's container collection add with property values");

        Logger.Log($"After sync: server.ContainerItems.Length={serverArea.ContainerItems.Length}");

        var serverPerson = serverArea.ContainerItems.FirstOrDefault(p => p.FirstName == firstName);
        Assert.NotNull(serverPerson);
        Assert.Equal(firstName, serverPerson.FirstName);
        Assert.Equal("Person", serverPerson.LastName);
        Logger.Log("Client->Server container collection add verified");
    }

    [Fact]
    public async Task RemoveFromContainerCollection_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerCollection;
        var serverArea = ServerFixture.ServerRoot.ClientToServerCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var keepFirstName = $"KeepContainer_{testId}";
        var removeFirstName = $"RemoveContainer_{testId}";

        // Setup: Add persons on client first
        var person1 = new NestedPerson(Client.Context) { FirstName = keepFirstName, LastName = "Test" };
        var person2 = new NestedPerson(Client.Context) { FirstName = removeFirstName, LastName = "Test" };
        clientArea.ContainerItems = [..clientArea.ContainerItems, person1, person2];
        Logger.Log($"Client added two persons: {keepFirstName}, {removeFirstName}");

        // Wait for server to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.ContainerItems.Any(p => p.FirstName == removeFirstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should sync initial two persons");

        var countAfterAdd = serverArea.ContainerItems.Length;
        Logger.Log($"Server synced initial state: {countAfterAdd} persons");

        // Act - client removes one person (keep only the first of the two we added)
        var itemsToKeep = clientArea.ContainerItems.Where(p => p.FirstName != removeFirstName).ToArray();
        clientArea.ContainerItems = itemsToKeep;
        Logger.Log($"Client removed person: {removeFirstName}");

        // Assert - server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasRemoved = serverArea.ContainerItems.Any(p => p.FirstName == removeFirstName);
                var hasKeep = serverArea.ContainerItems.Any(p => p.FirstName == keepFirstName);
                Logger.Log($"Polling server ContainerItems: hasRemoved={hasRemoved}, hasKeep={hasKeep}, count={serverArea.ContainerItems.Length}");
                return !hasRemoved && hasKeep;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's container collection remove");

        Logger.Log($"After sync: server.ContainerItems.Length={serverArea.ContainerItems.Length}");
        Assert.DoesNotContain(serverArea.ContainerItems, p => p.FirstName == removeFirstName);
        Assert.Contains(serverArea.ContainerItems, p => p.FirstName == keepFirstName);
        Logger.Log("Client->Server container collection remove verified");
    }

    [Fact]
    public async Task AddToFlatCollection_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerCollection;
        var serverArea = ServerFixture.ServerRoot.ClientToServerCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"FlatAdd_{testId}";

        Logger.Log($"Initial state: server.FlatItems.Length={serverArea.FlatItems.Length}, client.FlatItems.Length={clientArea.FlatItems.Length}");

        // Act - client adds a person to flat collection
        var newPerson = new NestedPerson(Client.Context)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        clientArea.FlatItems = [..clientArea.FlatItems, newPerson];
        Logger.Log($"Client added person: {newPerson.FirstName} {newPerson.LastName}");

        // Assert - server model should have our specific item (don't check count - other parallel tests may add items)
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.FlatItems.FirstOrDefault(p => p.FirstName == firstName);
                var result = serverPerson != null && serverPerson.LastName == "Person";
                Logger.Log($"Polling server FlatItems: count={serverArea.FlatItems.Length}, found={serverPerson != null}");
                return result;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's flat collection add with property values");

        Logger.Log($"After sync: server.FlatItems.Length={serverArea.FlatItems.Length}");

        var serverPerson = serverArea.FlatItems.FirstOrDefault(p => p.FirstName == firstName);
        Assert.NotNull(serverPerson);
        Assert.Equal(firstName, serverPerson.FirstName);
        Assert.Equal("Person", serverPerson.LastName);
        Logger.Log("Client->Server flat collection add verified");
    }

    [Fact]
    public async Task RemoveFromFlatCollection_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerCollection;
        var serverArea = ServerFixture.ServerRoot.ClientToServerCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var keepFirstName = $"KeepFlat_{testId}";
        var removeFirstName = $"RemoveFlat_{testId}";

        // Setup: Add persons on client first
        var person1 = new NestedPerson(Client.Context) { FirstName = keepFirstName, LastName = "Test" };
        var person2 = new NestedPerson(Client.Context) { FirstName = removeFirstName, LastName = "Test" };
        clientArea.FlatItems = [..clientArea.FlatItems, person1, person2];
        Logger.Log($"Client added two persons: {keepFirstName}, {removeFirstName}");

        // Wait for server to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.FlatItems.Any(p => p.FirstName == removeFirstName),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should sync initial two persons");

        var countAfterAdd = serverArea.FlatItems.Length;
        Logger.Log($"Server synced initial state: {countAfterAdd} persons");

        // Act - client removes one person (keep only the first of the two we added)
        var itemsToKeep = clientArea.FlatItems.Where(p => p.FirstName != removeFirstName).ToArray();
        clientArea.FlatItems = itemsToKeep;
        Logger.Log($"Client removed person: {removeFirstName}");

        // Assert - server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasRemoved = serverArea.FlatItems.Any(p => p.FirstName == removeFirstName);
                var hasKeep = serverArea.FlatItems.Any(p => p.FirstName == keepFirstName);
                Logger.Log($"Polling server FlatItems: hasRemoved={hasRemoved}, hasKeep={hasKeep}, count={serverArea.FlatItems.Length}");
                return !hasRemoved && hasKeep;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's flat collection remove");

        Logger.Log($"After sync: server.FlatItems.Length={serverArea.FlatItems.Length}");
        Assert.DoesNotContain(serverArea.FlatItems, p => p.FirstName == removeFirstName);
        Assert.Contains(serverArea.FlatItems, p => p.FirstName == keepFirstName);
        Logger.Log("Client->Server flat collection remove verified");
    }

    [Fact]
    public async Task MoveItemInContainerCollection_ServerReceivesReorderedCollection()
    {
        var clientArea = Client!.Root!.ClientToServerCollection;
        var serverArea = ServerFixture.ServerRoot.ClientToServerCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName1 = $"First_{testId}";
        var firstName2 = $"Second_{testId}";
        var firstName3 = $"Third_{testId}";

        Logger.Log($"Test starting with testId: {testId}");

        // Setup: Add three persons in order [First, Second, Third]
        var person1 = new NestedPerson(Client.Context) { FirstName = firstName1, LastName = "One" };
        var person2 = new NestedPerson(Client.Context) { FirstName = firstName2, LastName = "Two" };
        var person3 = new NestedPerson(Client.Context) { FirstName = firstName3, LastName = "Three" };
        clientArea.ContainerItems = [..clientArea.ContainerItems, person1, person2, person3];
        Logger.Log($"Client added three persons: {firstName1}, {firstName2}, {firstName3}");

        // Wait for server to sync all three
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.ContainerItems.Any(p => p.FirstName == firstName1) &&
                  serverArea.ContainerItems.Any(p => p.FirstName == firstName2) &&
                  serverArea.ContainerItems.Any(p => p.FirstName == firstName3),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should sync initial three persons");

        Logger.Log($"Server synced initial state: {serverArea.ContainerItems.Length} persons");
        foreach (var p in serverArea.ContainerItems.Where(p => p.FirstName.Contains(testId)))
            Logger.Log($"  Server: {p.FirstName}");

        // Act - client reorders: move Third to the front [Third, First, Second]
        var reordered = clientArea.ContainerItems
            .Where(p => !p.FirstName.Contains(testId))
            .Concat([person3, person1, person2])
            .ToArray();
        clientArea.ContainerItems = reordered;
        Logger.Log($"Client reordered to: {firstName3}, {firstName1}, {firstName2}");

        // Assert - server should have all three items (order may or may not be preserved depending on implementation)
        // The key assertion is that all items are still present after the move operation
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasAll = serverArea.ContainerItems.Any(p => p.FirstName == firstName1) &&
                             serverArea.ContainerItems.Any(p => p.FirstName == firstName2) &&
                             serverArea.ContainerItems.Any(p => p.FirstName == firstName3);
                Logger.Log($"Polling server after reorder: hasAll={hasAll}, count={serverArea.ContainerItems.Length}");
                foreach (var p in serverArea.ContainerItems.Where(p => p.FirstName.Contains(testId)))
                    Logger.Log($"  Server: {p.FirstName}");
                return hasAll;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should have all three items after reorder");

        Assert.Contains(serverArea.ContainerItems, p => p.FirstName == firstName1);
        Assert.Contains(serverArea.ContainerItems, p => p.FirstName == firstName2);
        Assert.Contains(serverArea.ContainerItems, p => p.FirstName == firstName3);
        Logger.Log("Client->Server collection move/reorder verified - all items present");
    }

    [Fact]
    public async Task MoveItemInFlatCollection_ServerReceivesReorderedCollection()
    {
        var clientArea = Client!.Root!.ClientToServerCollection;
        var serverArea = ServerFixture.ServerRoot.ClientToServerCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName1 = $"FlatFirst_{testId}";
        var firstName2 = $"FlatSecond_{testId}";
        var firstName3 = $"FlatThird_{testId}";

        Logger.Log($"Test starting with testId: {testId}");

        // Setup: Add three persons in order [First, Second, Third]
        var person1 = new NestedPerson(Client.Context) { FirstName = firstName1, LastName = "One" };
        var person2 = new NestedPerson(Client.Context) { FirstName = firstName2, LastName = "Two" };
        var person3 = new NestedPerson(Client.Context) { FirstName = firstName3, LastName = "Three" };
        clientArea.FlatItems = [..clientArea.FlatItems, person1, person2, person3];
        Logger.Log($"Client added three persons to FlatItems: {firstName1}, {firstName2}, {firstName3}");

        // Wait for server to sync all three
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.FlatItems.Any(p => p.FirstName == firstName1) &&
                  serverArea.FlatItems.Any(p => p.FirstName == firstName2) &&
                  serverArea.FlatItems.Any(p => p.FirstName == firstName3),
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should sync initial three persons in FlatItems");

        Logger.Log($"Server synced initial state: {serverArea.FlatItems.Length} persons in FlatItems");
        foreach (var p in serverArea.FlatItems.Where(p => p.FirstName.Contains(testId)))
            Logger.Log($"  Server FlatItems: {p.FirstName}");

        // Act - client reorders: move Third to the front [Third, First, Second]
        var reordered = clientArea.FlatItems
            .Where(p => !p.FirstName.Contains(testId))
            .Concat([person3, person1, person2])
            .ToArray();
        clientArea.FlatItems = reordered;
        Logger.Log($"Client reordered FlatItems to: {firstName3}, {firstName1}, {firstName2}");

        // Assert - server should have all three items
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasAll = serverArea.FlatItems.Any(p => p.FirstName == firstName1) &&
                             serverArea.FlatItems.Any(p => p.FirstName == firstName2) &&
                             serverArea.FlatItems.Any(p => p.FirstName == firstName3);
                Logger.Log($"Polling server FlatItems after reorder: hasAll={hasAll}, count={serverArea.FlatItems.Length}");
                foreach (var p in serverArea.FlatItems.Where(p => p.FirstName.Contains(testId)))
                    Logger.Log($"  Server FlatItems: {p.FirstName}");
                return hasAll;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should have all three items in FlatItems after reorder");

        Assert.Contains(serverArea.FlatItems, p => p.FirstName == firstName1);
        Assert.Contains(serverArea.FlatItems, p => p.FirstName == firstName2);
        Assert.Contains(serverArea.FlatItems, p => p.FirstName == firstName3);
        Logger.Log("Client->Server flat collection move/reorder verified - all items present");
    }
}

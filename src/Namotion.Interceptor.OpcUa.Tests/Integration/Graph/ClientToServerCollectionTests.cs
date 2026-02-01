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
    public async Task AddToContainerCollection_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerCollection;
        var serverArea = ServerFixture.ServerRoot.ClientToServerCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var firstName = $"ContainerAdd_{testId}";

        var initialServerCount = serverArea.ContainerItems.Length;
        Logger.Log($"Initial state: server.ContainerItems.Length={initialServerCount}, client.ContainerItems.Length={clientArea.ContainerItems.Length}");

        // Act - client adds a person to container collection
        var newPerson = new NestedPerson(Client.Context)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        clientArea.ContainerItems = [..clientArea.ContainerItems, newPerson];
        Logger.Log($"Client added person: {newPerson.FirstName} {newPerson.LastName}");

        // Assert - server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverCount = serverArea.ContainerItems.Length;
                if (serverCount != initialServerCount + 1) return false;
                var lastPerson = serverArea.ContainerItems.LastOrDefault();
                var result = lastPerson?.FirstName == firstName;
                Logger.Log($"Polling server ContainerItems: count={serverCount}, lastFirstName={lastPerson?.FirstName ?? "null"}");
                return result;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's container collection add with property values");

        Logger.Log($"After sync: server.ContainerItems.Length={serverArea.ContainerItems.Length}");
        Assert.Equal(initialServerCount + 1, serverArea.ContainerItems.Length);

        var serverPerson = serverArea.ContainerItems.LastOrDefault();
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

        var initialServerCount = serverArea.FlatItems.Length;
        Logger.Log($"Initial state: server.FlatItems.Length={initialServerCount}, client.FlatItems.Length={clientArea.FlatItems.Length}");

        // Act - client adds a person to flat collection
        var newPerson = new NestedPerson(Client.Context)
        {
            FirstName = firstName,
            LastName = "Person"
        };
        clientArea.FlatItems = [..clientArea.FlatItems, newPerson];
        Logger.Log($"Client added person: {newPerson.FirstName} {newPerson.LastName}");

        // Assert - server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverCount = serverArea.FlatItems.Length;
                if (serverCount != initialServerCount + 1) return false;
                var lastPerson = serverArea.FlatItems.LastOrDefault();
                var result = lastPerson?.FirstName == firstName;
                Logger.Log($"Polling server FlatItems: count={serverCount}, lastFirstName={lastPerson?.FirstName ?? "null"}");
                return result;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's flat collection add with property values");

        Logger.Log($"After sync: server.FlatItems.Length={serverArea.FlatItems.Length}");
        Assert.Equal(initialServerCount + 1, serverArea.FlatItems.Length);

        var serverPerson = serverArea.FlatItems.LastOrDefault();
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
}

using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for client-to-server nested property synchronization.
/// Verifies that property changes on nested subjects (items in collections, references)
/// propagate from client to server.
/// </summary>
[Trait("Category", "Integration")]
public class ClientToServerNestedPropertyTests : SharedServerTestBase
{
    public ClientToServerNestedPropertyTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    [Fact]
    public async Task ModifyPropertyOnCollectionItem_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerCollection;
        var serverArea = ServerFixture.ServerRoot.ClientToServerCollection;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var initialFirstName = $"Initial_{testId}";
        var updatedFirstName = $"Updated_{testId}";

        Logger.Log($"Test starting with testId: {testId}");

        // Setup: Add a person to the collection
        var person = new NestedPerson(Client.Context)
        {
            FirstName = initialFirstName,
            LastName = "Test"
        };
        clientArea.ContainerItems = [..clientArea.ContainerItems, person];
        Logger.Log($"Client added person with FirstName={initialFirstName}");

        // Wait for server to sync the initial person
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.ContainerItems.FirstOrDefault(p => p.FirstName == initialFirstName);
                Logger.Log($"Polling server for initial person: {serverPerson?.FirstName ?? "null"}");
                return serverPerson != null;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should sync initial person");

        Logger.Log($"Server synced initial person: {initialFirstName}");

        // Act - client modifies the property on the existing collection item
        person.FirstName = updatedFirstName;
        Logger.Log($"Client changed FirstName to: {updatedFirstName}");

        // Assert - server should receive the property change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.ContainerItems.FirstOrDefault(p => p.FirstName == updatedFirstName);
                Logger.Log($"Polling server for updated person: found={serverPerson != null}");
                foreach (var p in serverArea.ContainerItems.Where(p => p.FirstName.Contains(testId)))
                    Logger.Log($"  Server: {p.FirstName}");
                return serverPerson != null;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive property change on collection item");

        Assert.Contains(serverArea.ContainerItems, p => p.FirstName == updatedFirstName);
        Logger.Log("Client->Server nested property change on collection item verified");
    }

    [Fact]
    public async Task ModifyPropertyOnDictionaryItem_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerDictionary;
        var serverArea = ServerFixture.ServerRoot.ClientToServerDictionary;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var testKey = $"nestedprop_{testId}";
        var initialFirstName = $"DictInitial_{testId}";
        var updatedFirstName = $"DictUpdated_{testId}";

        Logger.Log($"Test starting with key: {testKey}");

        // Setup: Add a person to the dictionary
        var person = new NestedPerson(Client.Context)
        {
            FirstName = initialFirstName,
            LastName = "Test"
        };
        clientArea.Items = new Dictionary<string, NestedPerson>(clientArea.Items ?? new())
        {
            [testKey] = person
        };
        Logger.Log($"Client added person at key '{testKey}' with FirstName={initialFirstName}");

        // Wait for server to sync the initial person
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.Items?.GetValueOrDefault(testKey);
                Logger.Log($"Polling server for initial person: {serverPerson?.FirstName ?? "null"}");
                return serverPerson?.FirstName == initialFirstName;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should sync initial dictionary entry");

        Logger.Log($"Server synced initial person: {initialFirstName}");

        // Act - client modifies the property on the existing dictionary item
        person.FirstName = updatedFirstName;
        Logger.Log($"Client changed FirstName to: {updatedFirstName}");

        // Assert - server should receive the property change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.Items?.GetValueOrDefault(testKey);
                Logger.Log($"Polling server for updated person: {serverPerson?.FirstName ?? "null"}");
                return serverPerson?.FirstName == updatedFirstName;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive property change on dictionary item");

        Assert.Equal(updatedFirstName, serverArea.Items![testKey].FirstName);
        Logger.Log("Client->Server nested property change on dictionary item verified");
    }

    [Fact]
    public async Task ModifyPropertyOnReference_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerReference;
        var serverArea = ServerFixture.ServerRoot.ClientToServerReference;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var initialFirstName = $"RefInitial_{testId}";
        var updatedFirstName = $"RefUpdated_{testId}";

        Logger.Log($"Test starting with testId: {testId}");

        // Setup: Assign a person reference
        var person = new NestedPerson(Client.Context)
        {
            FirstName = initialFirstName,
            LastName = "Test"
        };
        clientArea.Person = person;
        Logger.Log($"Client assigned Person with FirstName={initialFirstName}");

        // Wait for server to sync the initial person
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.Person;
                Logger.Log($"Polling server for initial person: {serverPerson?.FirstName ?? "null"}");
                return serverPerson?.FirstName == initialFirstName;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should sync initial reference");

        Logger.Log($"Server synced initial person: {initialFirstName}");

        // Act - client modifies the property on the referenced subject
        person.FirstName = updatedFirstName;
        Logger.Log($"Client changed FirstName to: {updatedFirstName}");

        // Assert - server should receive the property change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverPerson = serverArea.Person;
                Logger.Log($"Polling server for updated person: {serverPerson?.FirstName ?? "null"}");
                return serverPerson?.FirstName == updatedFirstName;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive property change on reference");

        Assert.Equal(updatedFirstName, serverArea.Person?.FirstName);
        Logger.Log("Client->Server nested property change on reference verified");
    }
}

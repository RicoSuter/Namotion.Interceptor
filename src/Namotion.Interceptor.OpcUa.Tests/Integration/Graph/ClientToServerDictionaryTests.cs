using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for client-to-server dictionary synchronization.
/// Verifies that dictionary changes on client propagate to server model.
/// </summary>
[Trait("Category", "Integration")]
public class ClientToServerDictionaryTests : SharedServerTestBase
{
    public ClientToServerDictionaryTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    [Fact]
    public async Task AddEntry_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerDictionary;
        var serverArea = ServerFixture.ServerRoot.ClientToServerDictionary;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var testKey = $"clientkey_{testId}";

        Logger.Log($"Test starting with unique key: {testKey}");
        Logger.Log($"Client Items count before: {clientArea.Items?.Count ?? 0}");
        Logger.Log($"Server Items count before: {serverArea.Items?.Count ?? 0}");

        // Act - client adds to dictionary
        var newPerson = new NestedPerson(Client.Context)
        {
            FirstName = "DictClient",
            LastName = "Added"
        };
        clientArea.Items = new Dictionary<string, NestedPerson>(clientArea.Items ?? new())
        {
            [testKey] = newPerson
        };
        Logger.Log($"Client added '{testKey}' to dictionary");

        // Assert - server receives change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasKey = serverArea.Items?.ContainsKey(testKey) ?? false;
                Logger.Log($"Polling server Items for key '{testKey}': {hasKey}");
                return hasKey;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's dictionary add");

        Logger.Log($"After sync: server.Items contains '{testKey}'");
        Assert.True(serverArea.Items?.ContainsKey(testKey));
        Assert.Equal("DictClient", serverArea.Items![testKey].FirstName);
        Logger.Log("Client->Server dictionary add verified");
    }

    [Fact]
    public async Task RemoveEntry_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerDictionary;
        var serverArea = ServerFixture.ServerRoot.ClientToServerDictionary;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var keepKey = $"keep_{testId}";
        var removeKey = $"remove_{testId}";

        Logger.Log($"Test starting with keys: keep='{keepKey}', remove='{removeKey}'");

        // Setup - client adds two dictionary entries
        var person1 = new NestedPerson(Client.Context) { FirstName = "KeepDictC", LastName = "Entry" };
        var person2 = new NestedPerson(Client.Context) { FirstName = "RemoveDictC", LastName = "Entry" };
        clientArea.Items = new Dictionary<string, NestedPerson>
        {
            [keepKey] = person1,
            [removeKey] = person2
        };
        Logger.Log("Client added two dictionary entries");

        // Wait for server to sync initial entries
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasKeep = serverArea.Items?.ContainsKey(keepKey) ?? false;
                var hasRemove = serverArea.Items?.ContainsKey(removeKey) ?? false;
                Logger.Log($"Polling server for setup: keep={hasKeep}, remove={hasRemove}");
                return hasKeep && hasRemove;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should sync initial dictionary entries");

        Logger.Log($"Server synced initial state: {serverArea.Items?.Count} entries");

        // Act - client removes one entry
        var keepPerson = clientArea.Items![keepKey];
        clientArea.Items = new Dictionary<string, NestedPerson>
        {
            [keepKey] = keepPerson
        };
        Logger.Log($"Client removed '{removeKey}' key");

        // Assert - server receives change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasRemove = serverArea.Items?.ContainsKey(removeKey) ?? false;
                Logger.Log($"Polling server for removal: hasRemove={hasRemove}");
                return !hasRemove;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's dictionary remove");

        Logger.Log($"After sync: server.Items does not contain '{removeKey}'");
        Assert.False(serverArea.Items?.ContainsKey(removeKey));
        Assert.True(serverArea.Items?.ContainsKey(keepKey));
        Logger.Log("Client->Server dictionary remove verified");
    }

    [Fact]
    public async Task ReplaceEntry_ServerReceivesChange()
    {
        var clientArea = Client!.Root!.ClientToServerDictionary;
        var serverArea = ServerFixture.ServerRoot.ClientToServerDictionary;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var testKey = $"replacekey_{testId}";
        var firstName1 = $"Original_{testId}";
        var firstName2 = $"Replaced_{testId}";

        Logger.Log($"Test starting with key: '{testKey}', names: {firstName1} -> {firstName2}");

        // Setup - client adds initial entry
        var originalPerson = new NestedPerson(Client.Context)
        {
            FirstName = firstName1,
            LastName = "Original"
        };
        clientArea.Items = new Dictionary<string, NestedPerson>(clientArea.Items ?? new())
        {
            [testKey] = originalPerson
        };
        Logger.Log($"Client added '{testKey}' with FirstName={firstName1}");

        // Wait for server to sync initial entry
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var person = serverArea.Items?.GetValueOrDefault(testKey);
                Logger.Log($"Polling server for initial: {person?.FirstName ?? "null"}");
                return person?.FirstName == firstName1;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should sync initial dictionary entry");

        Logger.Log($"Server synced initial entry: {serverArea.Items![testKey].FirstName}");

        // Act - client replaces the value at the same key with a different subject
        var replacementPerson = new NestedPerson(Client.Context)
        {
            FirstName = firstName2,
            LastName = "Replaced"
        };
        clientArea.Items = new Dictionary<string, NestedPerson>(clientArea.Items)
        {
            [testKey] = replacementPerson
        };
        Logger.Log($"Client replaced '{testKey}' with FirstName={firstName2}");

        // Assert - server receives the replacement
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var person = serverArea.Items?.GetValueOrDefault(testKey);
                Logger.Log($"Polling server for replacement: {person?.FirstName ?? "null"}");
                return person?.FirstName == firstName2;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive dictionary value replacement");

        Assert.True(serverArea.Items?.ContainsKey(testKey));
        Assert.Equal(firstName2, serverArea.Items![testKey].FirstName);
        Assert.Equal("Replaced", serverArea.Items[testKey].LastName);
        Logger.Log("Client->Server dictionary replace verified");
    }
}

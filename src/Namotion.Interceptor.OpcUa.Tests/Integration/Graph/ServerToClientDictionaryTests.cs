using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for server-to-client dictionary synchronization.
/// Verifies that dictionary changes on server propagate to client model.
/// </summary>
[Trait("Category", "Integration")]
public class ServerToClientDictionaryTests : SharedServerTestBase
{
    public ServerToClientDictionaryTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    [Fact]
    public async Task AddEntry_ClientReceivesChange()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientDictionary;
        var clientArea = Client!.Root!.ServerToClientDictionary;

        // Use unique test identifier for dictionary key
        var testId = Guid.NewGuid().ToString("N")[..8];
        var key = $"entry_{testId}";
        var firstName = $"Dict_{testId}";

        Logger.Log($"Test starting with unique key: {key}");
        Logger.Log($"Server Items count before: {serverArea.Items?.Count ?? 0}");
        Logger.Log($"Client Items count before: {clientArea.Items?.Count ?? 0}");

        // Act - server adds dictionary entry
        serverArea.Items = new Dictionary<string, NestedPerson>
        {
            [key] = new NestedPerson(ServerFixture.ServerContext)
            {
                FirstName = firstName,
                LastName = "Entry"
            }
        };
        Logger.Log($"Server added entry with key '{key}'");

        // Assert - client receives change
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientItems = clientArea.Items;
                if (clientItems == null || clientItems.Count == 0) return false;

                Logger.Log($"Polling client Items count: {clientItems.Count}");
                return clientItems.ContainsKey(key) && clientItems[key].FirstName == firstName;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive dictionary entry addition");

        Assert.NotNull(clientArea.Items);
        Assert.True(clientArea.Items.ContainsKey(key));
        Assert.Equal(firstName, clientArea.Items[key].FirstName);
        Assert.Equal("Entry", clientArea.Items[key].LastName);
        Logger.Log($"Test passed - client has entry with key '{key}'");
    }

    [Fact]
    public async Task RemoveEntry_ClientReceivesChange()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientDictionary;
        var clientArea = Client!.Root!.ServerToClientDictionary;

        // Use unique test identifiers
        var testId = Guid.NewGuid().ToString("N")[..8];
        var keepKey = $"keep_{testId}";
        var removeKey = $"remove_{testId}";

        Logger.Log($"Test starting - keepKey: {keepKey}, removeKey: {removeKey}");

        // Setup - add two entries to the dictionary
        var keepPerson = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = $"Keep_{testId}",
            LastName = "Entry"
        };
        var removePerson = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = $"Remove_{testId}",
            LastName = "Entry"
        };
        serverArea.Items = new Dictionary<string, NestedPerson>
        {
            [keepKey] = keepPerson,
            [removeKey] = removePerson
        };
        Logger.Log($"Server added two entries");

        // Wait for client to receive both entries
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientItems = clientArea.Items;
                if (clientItems == null) return false;
                Logger.Log($"Polling client Items count: {clientItems.Count}");
                return clientItems.ContainsKey(keepKey) && clientItems.ContainsKey(removeKey);
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive both initial entries");

        Logger.Log($"Client received both entries");

        // Act - remove one entry from dictionary
        serverArea.Items = new Dictionary<string, NestedPerson>
        {
            [keepKey] = keepPerson
        };
        Logger.Log($"Server removed entry with key '{removeKey}'");

        // Assert - client receives the removal
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientItems = clientArea.Items;
                if (clientItems == null) return false;
                Logger.Log($"Polling client Items - count: {clientItems.Count}, has keepKey: {clientItems.ContainsKey(keepKey)}, has removeKey: {clientItems.ContainsKey(removeKey)}");
                return clientItems.ContainsKey(keepKey) && !clientItems.ContainsKey(removeKey);
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive entry removal");

        Assert.NotNull(clientArea.Items);
        Assert.True(clientArea.Items.ContainsKey(keepKey));
        Assert.False(clientArea.Items.ContainsKey(removeKey));
        Logger.Log($"Test passed - client has only entry with key '{keepKey}'");
    }

    [Fact]
    public async Task MultipleOperations_SequentialChanges()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientDictionary;
        var clientArea = Client!.Root!.ServerToClientDictionary;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var key1 = $"first_{testId}";
        var key2 = $"second_{testId}";
        var key3 = $"third_{testId}";

        Logger.Log($"Test starting with testId: {testId}");

        // Clear dictionary first
        serverArea.Items = new Dictionary<string, NestedPerson>();
        Logger.Log("Server cleared dictionary");

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientItems = clientArea.Items;
                // Wait until dictionary is empty or only has items from this test
                return clientItems == null || clientItems.Count == 0 ||
                       !clientItems.Keys.Any(k => !k.Contains(testId));
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should see empty dictionary");

        // Step 1: Add first entry
        var person1 = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = $"First_{testId}",
            LastName = "Entry"
        };
        serverArea.Items = new Dictionary<string, NestedPerson> { [key1] = person1 };
        Logger.Log($"Server added first entry with key '{key1}'");

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientItems = clientArea.Items;
                if (clientItems == null) return false;
                Logger.Log($"Step 1 - Polling client Items count: {clientItems.Count}");
                return clientItems.ContainsKey(key1);
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive first entry");
        Logger.Log("Step 1 complete - first entry synced");

        // Step 2: Add second entry (keep first)
        var person2 = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = $"Second_{testId}",
            LastName = "Entry"
        };
        serverArea.Items = new Dictionary<string, NestedPerson>
        {
            [key1] = person1,
            [key2] = person2
        };
        Logger.Log($"Server added second entry with key '{key2}'");

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientItems = clientArea.Items;
                if (clientItems == null) return false;
                Logger.Log($"Step 2 - Polling client Items count: {clientItems.Count}");
                return clientItems.ContainsKey(key1) && clientItems.ContainsKey(key2);
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive second entry");
        Logger.Log("Step 2 complete - two entries synced");

        // Step 3: Add third entry, remove first entry
        var person3 = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = $"Third_{testId}",
            LastName = "Entry"
        };
        serverArea.Items = new Dictionary<string, NestedPerson>
        {
            [key2] = person2,
            [key3] = person3
        };
        Logger.Log($"Server added '{key3}' and removed '{key1}'");

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientItems = clientArea.Items;
                if (clientItems == null) return false;
                Logger.Log($"Step 3 - Polling client Items count: {clientItems.Count}, has key2: {clientItems.ContainsKey(key2)}, has key3: {clientItems.ContainsKey(key3)}, has key1: {clientItems.ContainsKey(key1)}");
                return !clientItems.ContainsKey(key1) && clientItems.ContainsKey(key2) && clientItems.ContainsKey(key3);
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should see key1 removed and key3 added");
        Logger.Log("Step 3 complete - key swap synced");

        // Step 4: Clear all entries
        serverArea.Items = new Dictionary<string, NestedPerson>();
        Logger.Log("Server cleared all entries");

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientItems = clientArea.Items;
                if (clientItems == null) return true;
                Logger.Log($"Step 4 - Polling client Items count: {clientItems.Count}");
                return clientItems.Count == 0;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should see empty dictionary");

        Assert.NotNull(clientArea.Items);
        Assert.Empty(clientArea.Items);
        Logger.Log("Test passed - all sequential operations synced correctly");
    }

    [Fact]
    public async Task ReplaceEntry_ClientReceivesChange()
    {
        var serverArea = ServerFixture.ServerRoot.ServerToClientDictionary;
        var clientArea = Client!.Root!.ServerToClientDictionary;

        // Use unique test identifier
        var testId = Guid.NewGuid().ToString("N")[..8];
        var testKey = $"replacekey_{testId}";
        var firstName1 = $"Original_{testId}";
        var firstName2 = $"Replaced_{testId}";

        Logger.Log($"Test starting with key: '{testKey}', names: {firstName1} -> {firstName2}");

        // Setup - server adds initial entry
        var originalPerson = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName1,
            LastName = "Original"
        };
        serverArea.Items = new Dictionary<string, NestedPerson>
        {
            [testKey] = originalPerson
        };
        Logger.Log($"Server added '{testKey}' with FirstName={firstName1}");

        // Wait for client to sync initial entry
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var person = clientArea.Items?.GetValueOrDefault(testKey);
                Logger.Log($"Polling client for initial: {person?.FirstName ?? "null"}");
                return person?.FirstName == firstName1;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should sync initial dictionary entry");

        Logger.Log($"Client synced initial entry: {clientArea.Items![testKey].FirstName}");

        // Act - server replaces the value at the same key with a different subject
        var replacementPerson = new NestedPerson(ServerFixture.ServerContext)
        {
            FirstName = firstName2,
            LastName = "Replaced"
        };
        serverArea.Items = new Dictionary<string, NestedPerson>
        {
            [testKey] = replacementPerson
        };
        Logger.Log($"Server replaced '{testKey}' with FirstName={firstName2}");

        // Assert - client receives the replacement
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var person = clientArea.Items?.GetValueOrDefault(testKey);
                Logger.Log($"Polling client for replacement: {person?.FirstName ?? "null"}");
                return person?.FirstName == firstName2;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive dictionary value replacement");

        Assert.True(clientArea.Items?.ContainsKey(testKey));
        Assert.Equal(firstName2, clientArea.Items![testKey].FirstName);
        Assert.Equal("Replaced", clientArea.Items[testKey].LastName);
        Logger.Log("Server->Client dictionary replace verified");
    }
}

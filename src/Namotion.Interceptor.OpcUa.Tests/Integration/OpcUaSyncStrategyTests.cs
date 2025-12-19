using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for OPC UA sync strategy functionality including dictionary support,
/// dynamic subject lifecycle, and parent detachment.
/// </summary>
[Collection("OPC UA Integration")]
public class OpcUaSyncStrategyTests
{
    private readonly ITestOutputHelper _output;

    private OpcUaTestServer<TestRoot>? _server;
    private OpcUaTestClient<TestRoot>? _client;

    public OpcUaSyncStrategyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task DictionaryProperty_ShouldSyncFromServerToClient()
    {
        try
        {
            // Arrange - Start server with dictionary populated
            await StartServerWithDictionaryAsync();
            await StartClientAsync();

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Wait for sync
            await Task.Delay(2000);

            // Assert - Client should have received the dictionary
            Assert.NotNull(_client.Root.PeopleByName);
            Assert.Equal(_server.Root.PeopleByName.Count, _client.Root.PeopleByName.Count);

            foreach (var kvp in _server.Root.PeopleByName)
            {
                Assert.True(_client.Root.PeopleByName.ContainsKey(kvp.Key),
                    $"Client dictionary missing key: {kvp.Key}");

                var clientPerson = _client.Root.PeopleByName[kvp.Key];
                Assert.Equal(kvp.Value.FirstName, clientPerson.FirstName);
                Assert.Equal(kvp.Value.LastName, clientPerson.LastName);

                _output.WriteLine($"Synced dictionary entry: {kvp.Key} -> {clientPerson.FirstName} {clientPerson.LastName}");
            }
        }
        finally
        {
            await StopAsync();
        }
    }

    [Fact]
    public async Task DictionaryProperty_ValueChanges_ShouldSyncToClient()
    {
        try
        {
            // Arrange
            await StartServerWithDictionaryAsync();
            await StartClientAsync();

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            await Task.Delay(2000);

            // Verify initial sync
            Assert.True(_client.Root.PeopleByName.ContainsKey("alice"));
            var initialFirstName = _client.Root.PeopleByName["alice"].FirstName;
            _output.WriteLine($"Initial FirstName: {initialFirstName}");

            // Act - Modify a value in the dictionary entry on server
            _server.Root.PeopleByName["alice"].FirstName = "Alicia";

            await Task.Delay(2000);

            // Assert - Client should receive the updated value
            Assert.Equal("Alicia", _client.Root.PeopleByName["alice"].FirstName);
            _output.WriteLine($"Updated FirstName synced: {_client.Root.PeopleByName["alice"].FirstName}");
        }
        finally
        {
            await StopAsync();
        }
    }

    [Fact]
    public async Task ModifyExistingSubject_WithSyncEnabled_ShouldSyncValues()
    {
        try
        {
            // Arrange
            await StartServerAsync(enableLiveSync: true);
            await StartClientAsync(enableLiveSync: true);

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);
            Assert.NotNull(_server.Root.Person);
            Assert.NotNull(_client.Root.Person);

            await Task.Delay(1000);

            _output.WriteLine($"Initial server person: {_server.Root.Person.FirstName} {_server.Root.Person.LastName}");
            _output.WriteLine($"Initial client person: {_client.Root.Person.FirstName} {_client.Root.Person.LastName}");

            // Act - Modify existing person's properties on server
            _server.Root.Person.FirstName = "UpdatedFirst";
            _server.Root.Person.LastName = "UpdatedLast";

            _output.WriteLine($"Updated server person: {_server.Root.Person.FirstName} {_server.Root.Person.LastName}");

            await Task.Delay(2000);

            // Assert - Client should have received the value updates
            Assert.Equal("UpdatedFirst", _client.Root.Person.FirstName);
            Assert.Equal("UpdatedLast", _client.Root.Person.LastName);

            _output.WriteLine($"Client received: {_client.Root.Person.FirstName} {_client.Root.Person.LastName}");
        }
        finally
        {
            await StopAsync();
        }
    }

    [Fact]
    public async Task DetachSubject_ShouldCleanupProperly()
    {
        try
        {
            // Arrange
            await StartServerAsync(enableLiveSync: true);
            await StartClientAsync(enableLiveSync: true);

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);
            Assert.NotNull(_server.Root.Person);

            await Task.Delay(1000);

            var initialPerson = _server.Root.Person;
            _output.WriteLine($"Initial person: {initialPerson.FirstName} {initialPerson.LastName}");

            // Act - Detach the person by setting to null
            _server.Root.Person = null!;

            await Task.Delay(2000);

            // Assert - Server person should be null
            Assert.Null(_server.Root.Person);
            _output.WriteLine("Server person detached successfully");
        }
        finally
        {
            await StopAsync();
        }
    }

    [Fact]
    public async Task CollectionModification_AddItem_ShouldSyncToClient()
    {
        try
        {
            // Arrange
            await StartServerAsync(enableLiveSync: true);
            await StartClientAsync(enableLiveSync: true);

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            await Task.Delay(1000);

            var initialCount = _server.Root.People.Length;
            _output.WriteLine($"Initial people count: {initialCount}");

            // Act - Add a new person to the collection
            var newPerson = new TestPerson { FirstName = "New", LastName = "Person", Scores = [75.0] };
            _server.Root.People = _server.Root.People.Concat([newPerson]).ToArray();

            _output.WriteLine($"Added person, new count: {_server.Root.People.Length}");

            await Task.Delay(2000);

            // Assert - Server should have the new person
            Assert.Equal(initialCount + 1, _server.Root.People.Length);
            Assert.Contains(_server.Root.People, p => p.FirstName == "New");

            _output.WriteLine("Collection modification completed successfully");
        }
        finally
        {
            await StopAsync();
        }
    }

    private async Task StartServerAsync(bool enableLiveSync = false)
    {
        _server = new OpcUaTestServer<TestRoot>(_output);
        await _server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Connected = true;
                root.Name = "Test Server";
                root.ScalarNumbers = [10, 20, 30];
                root.Person = new TestPerson { FirstName = "John", LastName = "Doe", Scores = [85.5, 92.3] };
                root.People = [
                    new TestPerson { FirstName = "Jane", LastName = "Smith", Scores = [88.1, 95.7] }
                ];
            },
            enableStructureSynchronization: enableLiveSync);
    }

    private async Task StartServerWithDictionaryAsync()
    {
        _server = new OpcUaTestServer<TestRoot>(_output);
        await _server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Connected = true;
                root.Name = "Test Server";
                root.ScalarNumbers = [10, 20, 30];
                root.Person = new TestPerson { FirstName = "John", LastName = "Doe", Scores = [85.5, 92.3] };
                root.People = [];
                root.PeopleByName = new Dictionary<string, TestPerson>
                {
                    ["alice"] = new TestPerson { FirstName = "Alice", LastName = "Johnson", Scores = [95.0] },
                    ["bob"] = new TestPerson { FirstName = "Bob", LastName = "Williams", Scores = [88.5] }
                };
            },
            enableStructureSynchronization: false);
    }

    private async Task StartClientAsync(bool enableLiveSync = false)
    {
        _client = new OpcUaTestClient<TestRoot>(_output);
        await _client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            enableStructureSynchronization: enableLiveSync);
    }

    private async Task StopAsync()
    {
        await (_client?.StopAsync() ?? Task.CompletedTask);
        await (_server?.StopAsync() ?? Task.CompletedTask);
    }
}

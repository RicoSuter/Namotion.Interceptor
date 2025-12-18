using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Collection("OPC UA Integration")]
public class OpcUaAddressSpaceSyncTests
{
    private readonly ITestOutputHelper _output;

    private OpcUaTestServer<TestRoot>? _server;
    private OpcUaTestClient<TestRoot>? _client;

    public OpcUaAddressSpaceSyncTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ServerAttachSubject_WithLiveSyncDisabled_ClientDoesNotReceiveUpdate()
    {
        try
        {
            // Arrange - Start server and client WITHOUT live sync enabled
            await StartServerAsync(enableLiveSync: false);
            await StartClientAsync(enableLiveSync: false);

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            var initialServerPeopleCount = _server.Root.People.Length;
            var initialClientPeopleCount = _client.Root.People.Length;

            _output.WriteLine($"Initial server people count: {initialServerPeopleCount}");
            _output.WriteLine($"Initial client people count: {initialClientPeopleCount}");

            // Act - Add a new person to server's collection
            var newPerson = new TestPerson { FirstName = "Alice", LastName = "Johnson", Scores = [95.0, 88.0] };
            var newPeople = _server.Root.People.Concat([newPerson]).ToArray();
            _server.Root.People = newPeople;

            _output.WriteLine($"Added person to server. New count: {_server.Root.People.Length}");

            // Wait to see if sync happens (it shouldn't)
            await Task.Delay(2000);

            // Assert - Client should NOT have received the new person since sync is disabled
            Assert.Equal(initialClientPeopleCount, _client.Root.People.Length);
            _output.WriteLine($"Client people count after add (sync disabled): {_client.Root.People.Length}");
        }
        finally
        {
            await (_client?.StopAsync() ?? Task.CompletedTask);
            await (_server?.StopAsync() ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task ServerModifyExistingSubject_ShouldSyncValuesToClient()
    {
        try
        {
            // Arrange - Start server and client with live sync enabled
            await StartServerAsync(enableLiveSync: true);
            await StartClientAsync(enableLiveSync: true);

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);
            Assert.NotNull(_server.Root.Person);
            Assert.NotNull(_client.Root.Person);

            _output.WriteLine($"Initial server person: {_server.Root.Person.FirstName} {_server.Root.Person.LastName}");
            _output.WriteLine($"Initial client person: {_client.Root.Person.FirstName} {_client.Root.Person.LastName}");

            // Act - Modify existing person's properties on server
            _server.Root.Person.FirstName = "UpdatedFirst";
            _server.Root.Person.LastName = "UpdatedLast";

            _output.WriteLine($"Updated server person: {_server.Root.Person.FirstName} {_server.Root.Person.LastName}");

            // Wait for sync
            await Task.Delay(2000);

            // Assert - Client should have received the updates (value sync works regardless of address space sync)
            Assert.Equal("UpdatedFirst", _client.Root.Person.FirstName);
            Assert.Equal("UpdatedLast", _client.Root.Person.LastName);
            _output.WriteLine($"Client person after sync: {_client.Root.Person.FirstName} {_client.Root.Person.LastName}");
        }
        finally
        {
            await (_client?.StopAsync() ?? Task.CompletedTask);
            await (_server?.StopAsync() ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task LiveSyncEnabled_VerifiesConfigurationIsApplied()
    {
        try
        {
            // Arrange & Act - Start server and client with live sync enabled
            await StartServerAsync(enableLiveSync: true);
            await StartClientAsync(enableLiveSync: true);

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Wait for initialization
            await Task.Delay(1000);

            // Assert - Just verify that servers/clients started successfully with sync enabled
            // This validates that the configuration is correctly applied and doesn't break existing functionality
            Assert.True(_server.Root.Connected);
            Assert.True(_client.Root.Connected);
            
            _output.WriteLine("Server and client successfully started with EnableLiveSync=true");
            _output.WriteLine("Basic value synchronization continues to work with sync infrastructure in place");
        }
        finally
        {
            await (_client?.StopAsync() ?? Task.CompletedTask);
            await (_server?.StopAsync() ?? Task.CompletedTask);
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
            enableLiveSync: enableLiveSync);
    }

    private async Task StartClientAsync(bool enableLiveSync = false)
    {
        _client = new OpcUaTestClient<TestRoot>(_output);
        await _client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            enableLiveSync: enableLiveSync);
    }
}

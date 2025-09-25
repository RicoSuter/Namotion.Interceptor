using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Collection("OPC UA Integration")]
public class OpcUaServerClientReadWriteTests
{
    private readonly ITestOutputHelper _output;

    private OpcUaTestServer<TestRoot>? _server;
    private OpcUaTestClient<TestRoot>? _client;

    public OpcUaServerClientReadWriteTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WriteAndReadPrimitivesOnServer_ShouldUpdateClient()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            // Act & Assert
            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Test string property
            _server.Root.Name = "Updated Server Name";
            await Task.Delay(1000);
            Assert.Equal("Updated Server Name", _client.Root.Name);

            // Test numeric property
            _server.Root.Number = 123.45m;
            await Task.Delay(1000);
            Assert.Equal(123.45m, _client.Root.Number);
        }
        finally
        {
            await (_server?.StopAsync() ?? Task.CompletedTask);
            await (_client?.StopAsync() ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task WriteAndReadArraysOnServer_ShouldUpdateClient()
    {
        try
        {
            // Arrange - Start Server and Client
            await StartServerAsync();
            await StartClientAsync();

            // Act & Assert - Test basic array synchronization to validate valueRank
            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Test just one simple integer array
            _output.WriteLine($"Server initial ScalarNumbers: [{string.Join(", ", _server.Root.ScalarNumbers)}]");
            _output.WriteLine($"Client initial ScalarNumbers: [{string.Join(", ", _client.Root.ScalarNumbers)}]");

            var newNumbers = new[] { 100, 200, 300 };
            _server.Root.ScalarNumbers = newNumbers;
            _output.WriteLine($"Server updated ScalarNumbers: [{string.Join(", ", _server.Root.ScalarNumbers)}]");

            // Wait longer for synchronization
            await Task.Delay(8000);

            _output.WriteLine($"Client ScalarNumbers after update: [{string.Join(", ", _client.Root.ScalarNumbers)}]");

            // If this fails, it indicates that either:
            // 1. Server-client sync is not working, OR
            // 2. The valueRank is incorrect and arrays can't sync properly
            Assert.Equal(newNumbers, _client.Root.ScalarNumbers);
            _output.WriteLine($"✓ Basic array sync: [{string.Join(", ", _client.Root.ScalarNumbers)}]");
        }
        finally
        {
            await (_server?.StopAsync() ?? Task.CompletedTask);
            await (_client?.StopAsync() ?? Task.CompletedTask);
        }
    }

    private async Task StartServerAsync()
    {
        _server = new OpcUaTestServer<TestRoot>(_output);

        IInterceptorSubjectContext? serverContext = null;

        await _server.StartAsync(
            context =>
            {
                serverContext = context;
                return new TestRoot(context);
            },
            root =>
            {
                root.Connected = true;
                root.Name = "Foo bar";
                root.ScalarNumbers = [10, 20, 30, 40, 50];
                root.ScalarStrings = ["Server", "Test", "Array"];
                root.NestedNumbers = [[100, 200], [300, 400]];
                root.People =
                [
                    new TestPerson(serverContext!) { FirstName = "John", LastName = "Server", Scores = [85.5, 92.3] },
                    new TestPerson(serverContext!) { FirstName = "Jane", LastName = "Test", Scores = [88.1, 95.7] }
                ];
            });
    }

    private async Task StartClientAsync()
    {
        _client = new OpcUaTestClient<TestRoot>(_output);
        await _client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected);
    }
}
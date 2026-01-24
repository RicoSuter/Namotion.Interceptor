using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Trait("Category", "Integration")]
public class OpcUaServerClientReadWriteTests
{
    private readonly ITestOutputHelper _output;
    private TestLogger? _logger;

    private OpcUaTestServer<TestRoot>? _server;
    private OpcUaTestClient<TestRoot>? _client;
    private PortLease? _port;

    public OpcUaServerClientReadWriteTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WriteAndReadPrimitives_ShouldUpdateClient()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            // Act & Assert
            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Test string property on server
            _server.Root.Name = "Updated Server Name";
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.Name == "Updated Server Name",
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should receive server's name update");

            // Test string property on client
            _client.Root.Name = "Updated Client Name";
            await AsyncTestHelpers.WaitUntilAsync(
                () => _server.Root.Name == "Updated Client Name",
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should receive client's name update");

            // Test numeric property on server
            _server.Root.Number = 123.45m;
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.Number == 123.45m,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should receive server's number update");

            // Test numeric property on client
            _client.Root.Number = 54.321m;
            await AsyncTestHelpers.WaitUntilAsync(
                () => _server.Root.Number == 54.321m,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should receive client's number update");
        }
        finally
        {
            await (_client?.StopAsync() ?? Task.CompletedTask);
            await (_server?.StopAsync() ?? Task.CompletedTask);
            _port?.Dispose();
            _port = null;
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
            _logger!.Log($"Server initial ScalarNumbers: [{string.Join(", ", _server.Root.ScalarNumbers)}]");
            _logger.Log($"Client initial ScalarNumbers: [{string.Join(", ", _client.Root.ScalarNumbers)}]");

            var newNumbers = new[] { 100, 200, 300 };
            _server.Root.ScalarNumbers = newNumbers;
            _logger.Log($"Server updated ScalarNumbers: [{string.Join(", ", _server.Root.ScalarNumbers)}]");

            // Wait for array synchronization (longer timeout for CI environments)
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.ScalarNumbers.SequenceEqual(newNumbers),
                timeout: TimeSpan.FromSeconds(60),
                message: "Client should receive server's array update");

            _logger.Log($"Client ScalarNumbers after update: [{string.Join(", ", _client.Root.ScalarNumbers)}]");

            // If this fails, it indicates that either:
            // 1. Server-client sync is not working, OR
            // 2. The valueRank is incorrect and arrays can't sync properly
            Assert.Equal(newNumbers, _client.Root.ScalarNumbers);
            _logger.Log($"Basic array sync: [{string.Join(", ", _client.Root.ScalarNumbers)}]");
        }
        finally
        {
            await (_client?.StopAsync() ?? Task.CompletedTask);
            await (_server?.StopAsync() ?? Task.CompletedTask);
            _port?.Dispose();
            _port = null;
        }
    }

    private async Task StartServerAsync()
    {
        _logger = new TestLogger(_output);
        _port = await OpcUaTestPortPool.AcquireAsync();

        _server = new OpcUaTestServer<TestRoot>(_logger);
        await _server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Connected = true;
                root.Name = "Foo bar";
                root.ScalarNumbers = [10, 20, 30, 40, 50];
                root.ScalarStrings = ["Server", "Test", "Array"];
                root.Person = new TestPerson { FirstName = "John", LastName = "Smith", Scores = [1, 2] };
                root.People =
                [
                    new TestPerson { FirstName = "John", LastName = "Doe", Scores = [85.5, 92.3] },
                    new TestPerson { FirstName = "Jane", LastName = "Doe", Scores = [88.1, 95.7] }
                ];
            },
            baseAddress: _port.BaseAddress,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }

    private async Task StartClientAsync()
    {
        _client = new OpcUaTestClient<TestRoot>(_logger!);
        await _client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            serverUrl: _port!.ServerUrl,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }
}
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Trait("Category", "Integration")]
public class OpcUaTransactionTests
{
    private readonly ITestOutputHelper _output;
    private TestLogger? _logger;

    private OpcUaTestServer<TestRoot>? _server;
    private OpcUaTestClient<TestRoot>? _client;
    private PortLease? _port;

    public OpcUaTransactionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Transaction_CommitSingleProperty_ServerReceivesChangeOnlyAfterCommit()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            var initialName = _server.Root.Name;

            // Act
            using (var transaction = await _client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
            {
                _client.Root.Name = "Transaction Value";

                // Assert - server should still have old value before commit
                Assert.Equal(initialName, _server.Root.Name);

                await transaction.CommitAsync(CancellationToken.None);
            }

            // Wait for OPC UA sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => _server.Root.Name == "Transaction Value",
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should receive committed transaction value");
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
    public async Task Transaction_CommitMultipleProperties_ServerReceivesAllChangesOnlyAfterCommit()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            var initialName = _server.Root.Name;
            var initialNumber = _server.Root.Number;

            // Act
            using (var transaction = await _client.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
            {
                _client.Root.Name = "Multi-Property Test";
                _client.Root.Number = 123.45m;

                // Assert - server should still have old values before commit
                Assert.Equal(initialName, _server.Root.Name);
                Assert.Equal(initialNumber, _server.Root.Number);

                await transaction.CommitAsync(CancellationToken.None);
            }

            // Wait for OPC UA sync of all properties
            await AsyncTestHelpers.WaitUntilAsync(
                () => _server.Root.Name == "Multi-Property Test" && _server.Root.Number == 123.45m,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should receive all committed transaction values");

            // Assert - server should now have all new values after commit
            Assert.Equal("Multi-Property Test", _server.Root.Name);
            Assert.Equal(123.45m, _server.Root.Number);
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
                root.Name = "Initial Server Value";
                root.Number = 0m;
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

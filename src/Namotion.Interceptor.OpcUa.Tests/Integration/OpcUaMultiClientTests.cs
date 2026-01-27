using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit.Abstractions;
using Xunit.Extensions.AssemblyFixture;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for multi-client scenarios using the shared server.
/// Tests client→server sync with multiple concurrent clients via transactions.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaMultiClientTests : IAsyncLifetime, IAssemblyFixture<SharedOpcUaServerFixture>
{
    private readonly SharedOpcUaServerFixture _fixture;
    private readonly TestLogger _logger;

    private OpcUaTestClient<SharedTestModel>? _client1;
    private OpcUaTestClient<SharedTestModel>? _client2;

    public OpcUaMultiClientTests(SharedOpcUaServerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _logger = new TestLogger(output);
    }

    public async Task InitializeAsync()
    {
        // Create two independent clients sequentially to avoid connection issues
        _client1 = await _fixture.CreateClientAsync(_logger);
        _client2 = await _fixture.CreateClientAsync(_logger);
    }

    public async Task DisposeAsync()
    {
        if (_client1 != null) await _client1.DisposeAsync();
        if (_client2 != null) await _client2.DisposeAsync();
    }

    [Fact]
    public async Task BothClientsWrite_DifferentProperties_ServerReceivesBoth()
    {
        var serverArea = _fixture.ServerRoot.MultiClient;
        var client1Area = _client1!.Root!.MultiClient;
        var client2Area = _client2!.Root!.MultiClient;

        // Both clients write different properties via transactions
        using (var transaction1 = await _client1.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            client1Area.SharedValue = "client1-value";
            await transaction1.CommitAsync(CancellationToken.None);
        }

        using (var transaction2 = await _client2.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            client2Area.Counter = 42;
            await transaction2.CommitAsync(CancellationToken.None);
        }

        // Both should sync to server
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.SharedValue == "client1-value",
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive Client 1's SharedValue");

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Counter == 42,
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should receive Client 2's Counter");

        _logger.Log("Both clients' concurrent writes received by server");
    }

    [Fact]
    public async Task SequentialWrites_SameProperty_LastWriteWins()
    {
        var serverArea = _fixture.ServerRoot.MultiClient;
        var client1Area = _client1!.Root!.MultiClient;
        var client2Area = _client2!.Root!.MultiClient;

        // Client 1 writes first via transaction
        using (var transaction1 = await _client1.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            client1Area.LastWriter = "client1";
            await transaction1.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.LastWriter == "client1",
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should have client1's write");

        // Client 2 writes second via transaction
        using (var transaction2 = await _client2.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            client2Area.LastWriter = "client2";
            await transaction2.CommitAsync(CancellationToken.None);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.LastWriter == "client2",
            timeout: TimeSpan.FromSeconds(30),
            message: "Server should have client2's write (last write wins)");

        _logger.Log("Sequential writes with last-write-wins verified");
    }
}

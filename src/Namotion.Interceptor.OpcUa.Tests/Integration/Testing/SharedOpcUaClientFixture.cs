using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// xUnit class fixture that provides a shared OPC UA client per test class.
/// Used with IClassFixture to reuse the same client across all tests in a class.
/// </summary>
public class SharedOpcUaClientFixture : IAsyncLifetime
{
    private OpcUaTestClient<SharedTestModel>? _client;
    private readonly SemaphoreSlim _lock = new(1);

    /// <summary>
    /// Gets or creates a client connected to the shared server.
    /// Thread-safe - first caller creates the client, subsequent callers get the same instance.
    /// </summary>
    public async Task<OpcUaTestClient<SharedTestModel>> GetOrCreateClientAsync(
        SharedOpcUaServerFixture server, TestLogger logger)
    {
        if (_client != null) return _client;

        await _lock.WaitAsync();
        try
        {
            _client ??= await server.CreateClientAsync(logger);
            return _client;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }
}

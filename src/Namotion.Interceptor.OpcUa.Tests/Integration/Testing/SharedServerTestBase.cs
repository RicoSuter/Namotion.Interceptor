using Xunit.Abstractions;
using Xunit.Extensions.AssemblyFixture;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Base class for tests that use the shared OPC UA server with per-class client reuse.
/// </summary>
/// <typeparam name="TClient">The client type.</typeparam>
public abstract class SharedServerTestBase<TClient> : IAsyncLifetime,
    IAssemblyFixture<SharedOpcUaServerFixture>,
    IClassFixture<SharedOpcUaClientFixture>
    where TClient : class
{
    protected readonly SharedOpcUaServerFixture ServerFixture;
    protected readonly SharedOpcUaClientFixture ClientFixture;
    protected readonly ITestOutputHelper Output;
    protected readonly TestLogger Logger;
    protected TClient? Client;

    private readonly long _serverLogPosition;

    protected SharedServerTestBase(
        SharedOpcUaServerFixture serverFixture,
        SharedOpcUaClientFixture clientFixture,
        ITestOutputHelper output)
    {
        ServerFixture = serverFixture;
        ClientFixture = clientFixture;
        Output = output;
        Logger = new TestLogger(output);
        _serverLogPosition = serverFixture.ServerLog.Position;
    }

    public abstract Task InitializeAsync();

    public async Task DisposeAsync()
    {
        try
        {
            // Client is disposed by SharedOpcUaClientFixture at class level
            // Only dispose if Client is a different type (e.g., DynamicSubject client)
            if (Client is IAsyncDisposable asyncDisposable && Client is not OpcUaTestClient<SharedTestModel>)
            {
                await asyncDisposable.DisposeAsync();
            }
        }
        finally
        {
            WriteServerLog();
        }
    }

    private void WriteServerLog()
    {
        var lines = ServerFixture.ServerLog.ReadFrom(_serverLogPosition);
        if (lines.Length == 0)
        {
            return;
        }

        // Includes lines caused by test classes running in parallel; times count from the server start.
        Output.WriteLine("Shared server log while this test ran:");
        foreach (var line in lines)
        {
            Output.WriteLine(line);
        }
    }
}

/// <summary>
/// Base class for tests using SharedTestModel client with per-class client reuse.
/// </summary>
public abstract class SharedServerTestBase : SharedServerTestBase<OpcUaTestClient<SharedTestModel>>
{
    protected SharedServerTestBase(
        SharedOpcUaServerFixture serverFixture,
        SharedOpcUaClientFixture clientFixture,
        ITestOutputHelper output)
        : base(serverFixture, clientFixture, output)
    {
    }

    public override async Task InitializeAsync()
    {
        Client = await ClientFixture.GetOrCreateClientAsync(ServerFixture, Logger);
    }
}

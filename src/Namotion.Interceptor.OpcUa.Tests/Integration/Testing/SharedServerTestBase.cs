using Xunit.Abstractions;
using Xunit.Extensions.AssemblyFixture;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Base class for tests that use the shared OPC UA server.
/// Handles client creation and disposal automatically.
/// </summary>
public abstract class SharedServerTestBase<TClient> : IAsyncLifetime, IAssemblyFixture<SharedOpcUaServerFixture>
    where TClient : class
{
    protected readonly SharedOpcUaServerFixture Fixture;
    protected readonly ITestOutputHelper Output;
    protected readonly TestLogger Logger;
    protected TClient? Client;

    protected SharedServerTestBase(SharedOpcUaServerFixture fixture, ITestOutputHelper output)
    {
        Fixture = fixture;
        Output = output;
        Logger = new TestLogger(output);
    }

    public abstract Task InitializeAsync();

    public async Task DisposeAsync()
    {
        if (Client is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (Client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// Base class for tests using SharedTestModel client.
/// </summary>
public abstract class SharedServerTestBase : SharedServerTestBase<OpcUaTestClient<SharedTestModel>>
{
    protected SharedServerTestBase(SharedOpcUaServerFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    public override async Task InitializeAsync()
    {
        Client = await Fixture.CreateClientAsync(Logger);
    }
}

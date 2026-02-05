using Namotion.Interceptor.OpcUa.Client;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssemblyFixture;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Base class for tests that use the shared OPC UA server with a fresh client per test.
/// Each test method gets its own client connection for isolation.
/// </summary>
public abstract class SharedServerTestBase : IAsyncLifetime,
    IAssemblyFixture<SharedOpcUaServerFixture>
{
    protected readonly SharedOpcUaServerFixture ServerFixture;
    protected readonly ITestOutputHelper Output;
    protected readonly TestLogger Logger;
    protected OpcUaTestClient<SharedTestModel>? Client;

    protected SharedServerTestBase(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
    {
        ServerFixture = serverFixture;
        Output = output;
        Logger = new TestLogger(output);
    }

    public virtual async Task InitializeAsync()
    {
        Client = await ServerFixture.CreateClientAsync(Logger, ConfigureClient);
    }

    public virtual async Task DisposeAsync()
    {
        if (Client != null)
        {
            await Client.DisposeAsync();
            Client = null;
        }
    }

    /// <summary>
    /// Configures the client. Override to customize client settings.
    /// </summary>
    protected virtual void ConfigureClient(OpcUaClientConfiguration config)
    {
        config.EnableGraphChangePublishing = true;
        config.EnableGraphChangeSubscription = true;
    }
}

/// <summary>
/// Generic base class for tests that use a custom client type.
/// </summary>
/// <typeparam name="TClient">The type of OPC UA test client to use.</typeparam>
public abstract class SharedServerTestBase<TClient> : IAsyncLifetime,
    IAssemblyFixture<SharedOpcUaServerFixture>
    where TClient : class, IAsyncDisposable
{
    protected readonly SharedOpcUaServerFixture ServerFixture;
    protected readonly ITestOutputHelper Output;
    protected readonly TestLogger Logger;
    protected TClient? Client;

    protected SharedServerTestBase(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
    {
        ServerFixture = serverFixture;
        Output = output;
        Logger = new TestLogger(output);
    }

    public abstract Task InitializeAsync();

    public virtual async Task DisposeAsync()
    {
        if (Client != null)
        {
            await Client.DisposeAsync();
            Client = default;
        }
    }
}

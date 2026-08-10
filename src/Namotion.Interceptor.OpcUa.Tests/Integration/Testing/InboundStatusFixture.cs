using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

[InterceptorSubject]
public partial class InboundStatusRoot
{
    [Path("opc", "Child")]
    public partial InboundStatusChild? Child { get; set; }
}

[InterceptorSubject]
public partial class InboundStatusChild
{
    [Path("opc", "Value")]
    public partial string? Value { get; set; }

    /// <summary>A sibling, so a test can prove one property's failure does not take another down.</summary>
    [Path("opc", "Other")]
    public partial string? Other { get; set; }

    /// <summary>Decimal maps to Double on the wire, so this only round-trips if the path converts.</summary>
    [Path("opc", "DecimalValue")]
    public partial decimal DecimalValue { get; set; }
}

/// <summary>Throws from the inbound conversion when the incoming value equals a sentinel.</summary>
internal sealed class ThrowOnSentinelConverter(object sentinel) : OpcUaValueConverter
{
    public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property)
    {
        if (Equals(nodeValue, sentinel))
        {
            throw new InvalidOperationException($"Refusing to convert '{nodeValue}'.");
        }

        return base.ConvertToPropertyValue(nodeValue, property);
    }
}

/// <summary>Rejects a specific value on write, standing in for a validation interceptor.</summary>
internal sealed class ThrowOnValueInterceptor(object rejected) : IWriteInterceptor
{
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (Equals(context.NewValue, rejected))
        {
            throw new InvalidOperationException($"Refusing to accept '{context.NewValue}'.");
        }

        next(ref context);
    }
}

/// <summary>
/// A server and a connected client over <see cref="InboundStatusRoot"/>, plus the handles a test needs
/// to drive the server's nodes with <see cref="OpcUaNodeStatusDriver"/>.
/// </summary>
internal sealed class InboundStatusFixture : IAsyncDisposable
{
    private const string InitialValue = "initial";

    private readonly PortLease _port;
    private readonly OpcUaTestServer<InboundStatusRoot> _server;
    private readonly OpcUaTestClient<InboundStatusRoot> _client;

    private InboundStatusFixture(
        PortLease port,
        OpcUaTestServer<InboundStatusRoot> server,
        OpcUaTestClient<InboundStatusRoot> client)
    {
        _port = port;
        _server = server;
        _client = client;
    }

    public InboundStatusRoot ServerRoot => _server.Root!;

    public InboundStatusRoot ClientRoot => _client.Root!;

    public IOpcUaSubjectServer ServerService => _server.Server!;

    public ISubjectSource ClientSource => (ISubjectSource)_client.Source!;

    public PropertyReference ServerProperty =>
        new(ServerRoot.Child!, nameof(InboundStatusChild.Value));

    public PropertyReference OtherProperty =>
        new(ServerRoot.Child!, nameof(InboundStatusChild.Other));

    public PropertyReference DecimalProperty =>
        new(ServerRoot.Child!, nameof(InboundStatusChild.DecimalValue));

    public static async Task<InboundStatusFixture> StartAsync(
        ITestOutputHelper output,
        OpcUaValueConverter? valueConverter = null,
        IWriteInterceptor? clientInterceptor = null)
    {
        var logger = new TestLogger(output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        var server = new OpcUaTestServer<InboundStatusRoot>(logger);
        OpcUaTestClient<InboundStatusRoot>? client = null;
        try
        {
            await server.StartAsync(
                createRoot: context => new InboundStatusRoot(context),
                initializeDefaults: (context, root) =>
                    root.Child = new InboundStatusChild(context) { Value = InitialValue, Other = InitialValue },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            client = new OpcUaTestClient<InboundStatusRoot>(logger, configureClient: configuration =>
            {
                if (valueConverter is not null)
                {
                    configuration.ValueConverter = valueConverter;
                }
            });

            await client.StartAsync(
                createRoot: context =>
                {
                    if (clientInterceptor is not null)
                    {
                        context.AddService(clientInterceptor);
                    }

                    return new InboundStatusRoot(context);
                },
                isConnected: root => root.Child?.Value == InitialValue,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            // Wait for the node tree, so a test can drive it immediately.
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Server!.TryGetVariableNode(
                    new PropertyReference(server.Root!.Child!, nameof(InboundStatusChild.Value)), out _),
                message: "the child's variable node should exist");

            return new InboundStatusFixture(port, server, client);
        }
        catch
        {
            // A leaked lease would starve the port pool's parallel slots for the rest of the run.
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            await server.DisposeAsync();
            port.Dispose();
            throw;
        }
    }

    public Task WaitForClientValueAsync(string expected) =>
        AsyncTestHelpers.WaitUntilAsync(
            () => ClientRoot.Child?.Value == expected,
            message: $"the client should hold '{expected}'");

    /// <summary>Drives both string properties in one lock hold, so they arrive in a single notification.</summary>
    public void PublishPair(object? value, StatusCode valueStatus, object? other, StatusCode otherStatus) =>
        OpcUaNodeStatusDriver.PublishMany(
            ServerService,
            (ServerProperty, value, valueStatus),
            (OtherProperty, other, otherStatus));

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        _port.Dispose();
    }
}

using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.OpcUa.Attributes;
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

    /// <summary>
    /// Decimal maps to Double on the wire, so this only round-trips if the path converts. The Percent
    /// deadband is what routes it to the polling fallback: the server rejects the filter with
    /// BadMonitoredItemFilterUnsupported because the variable has no EURange child. Only numeric
    /// properties may carry it, and a non-numeric one is rejected with BadFilterNotAllowed instead,
    /// which this connector classifies as transient and keeps in the subscription for retry.
    /// </summary>
    [OpcUaNode("DecimalValue", DeadbandType = DeadbandType.Percent, DeadbandValue = 1.0)]
    public partial decimal DecimalValue { get; set; }

    /// <summary>Polled like DecimalValue, but needs no conversion, so a test can isolate the status handling.</summary>
    [OpcUaNode("DoubleValue", DeadbandType = DeadbandType.Percent, DeadbandValue = 1.0)]
    public partial double DoubleValue { get; set; }
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

/// <summary>Throws from the outbound conversion when the local value equals a sentinel.</summary>
internal sealed class ThrowOnOutboundSentinelConverter(object sentinel) : OpcUaValueConverter
{
    public override object? ConvertToNodeValue(object? propertyValue, RegisteredSubjectProperty property)
    {
        if (Equals(propertyValue, sentinel))
        {
            throw new InvalidOperationException($"Refusing to convert '{propertyValue}'.");
        }

        return base.ConvertToNodeValue(propertyValue, property);
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

    /// <summary>The two numeric properties carrying a Percent deadband, which the server cannot honour.</summary>
    private const int PolledPropertyCount = 2;

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

    public IInterceptorSubjectContext ClientContext => _client.Context;

    public PropertyReference ServerProperty =>
        new(ServerRoot.Child!, nameof(InboundStatusChild.Value));

    public PropertyReference ClientValueProperty =>
        new(ClientRoot.Child!, nameof(InboundStatusChild.Value));

    public PropertyReference OtherProperty =>
        new(ServerRoot.Child!, nameof(InboundStatusChild.Other));

    public PropertyReference DecimalProperty =>
        new(ServerRoot.Child!, nameof(InboundStatusChild.DecimalValue));

    public PropertyReference DoubleProperty =>
        new(ServerRoot.Child!, nameof(InboundStatusChild.DoubleValue));

    // waitForInitialValue: false only waits for the client's subscriptions, not for the initial value.
    // A test whose interceptor rejects that very value would otherwise fail here in its Arrange phase
    // rather than on its own assertion.
    public static async Task<InboundStatusFixture> StartAsync(
        ITestOutputHelper output,
        OpcUaValueConverter? valueConverter = null,
        IWriteInterceptor? clientInterceptor = null,
        bool waitForInitialValue = true,
        ILoggerProvider? extraClientLoggerProvider = null)
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

            client = new OpcUaTestClient<InboundStatusRoot>(logger, extraLoggerProvider: extraClientLoggerProvider, configureClient: configuration =>
            {
                // The minimum the configuration allows, so a polled property does not add seconds per test.
                configuration.PollingInterval = TimeSpan.FromMilliseconds(100);

                // Notifications are then processed one at a time in sequence order. A test that uses one
                // property's arrival as a barrier before asserting on another therefore holds even if the
                // two are split across notifications, because the queue order is the processing order.
                configuration.SubscriptionSequentialPublishing = true;

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
                isConnected: root => !waitForInitialValue || root.Child?.Value == InitialValue,
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

    /// <summary>
    /// Waits until both deadband-filtered properties have been moved to the polling fallback. Asserting
    /// this before any value keeps a future SDK that rejects the filter with a different status code
    /// (which keeps or drops the item instead of polling it) from surfacing as an unexplained value timeout.
    /// </summary>
    public Task WaitForPolledPropertiesAsync() =>
        AsyncTestHelpers.WaitUntilAsync(
            () => _client.Source!.Diagnostics.Polling?.ItemCount == PolledPropertyCount,
            message: $"the {PolledPropertyCount} deadband-filtered properties should have fallen back to polling");

    /// <summary>
    /// Caps the client's write requests at one node, which is how a two-property flush becomes two
    /// batches without a model of four thousand properties.
    /// </summary>
    public void LimitWritesToOneNodePerRequest() =>
        _client.Source!.CurrentSession!.OperationLimits.MaxNodesPerWrite = 1;

    /// <summary>
    /// The polled reads answered so far, which a test can use as a clock over poll cycles instead of
    /// waiting for a fixed span.
    /// </summary>
    public long PolledReadCount => _client.Source!.Diagnostics.Polling?.TotalSuccessfulReads ?? 0;

    public Task WaitForClientValueAsync(string expected) =>
        AsyncTestHelpers.WaitUntilAsync(
            () => ClientRoot.Child?.Value == expected,
            message: $"the client should hold '{expected}'");

    /// <summary>
    /// Drives both string properties in one lock hold, so they normally arrive in a single notification.
    /// The server's publish path does not take that lock, so co-delivery is the norm, not a guarantee.
    /// </summary>
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

using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

[InterceptorSubject]
public partial class ReadAfterWriteRoot
{
    [Path("opc", "Child")]
    public partial ReadAfterWriteChild? Child { get; set; }
}

[InterceptorSubject]
public partial class ReadAfterWriteChild
{
    /// <summary>
    /// SamplingInterval=0 asks for exception-based monitoring, which is what arms the read-after-write
    /// once the server revises it upwards. The Status trigger stops the subscription from ever reporting
    /// a value change, so a value the client did not write itself can only have come from the read-back.
    /// </summary>
    [OpcUaNode("Trigger", SamplingInterval = 0, DataChangeTrigger = DataChangeTrigger.Status)]
    public partial string? Trigger { get; set; }

    /// <summary>
    /// Armed for read-after-writes exactly like <see cref="Trigger"/>, but its node refuses every write.
    /// Written together with <see cref="Trigger"/> it makes the batch a partial failure, which is the
    /// only shape in which a refused change reaches the scheduling step at all.
    /// </summary>
    [OpcUaNode("Refused", SamplingInterval = 0, DataChangeTrigger = DataChangeTrigger.Status)]
    public partial string? Refused { get; set; }
}

/// <summary>
/// Holds an outbound write inside the client's own conversion step, which runs while the write request
/// is being built and before it is acknowledged. A test uses it to commit a second local write in that
/// window, which is the only window where the revision a read-back is ranked against differs depending
/// on whether it was captured at build time or read back at notify time.
/// </summary>
internal sealed class GatedValueConverter : OpcUaValueConverter, IDisposable
{
    private readonly ManualResetEventSlim _reached = new(false);
    private readonly ManualResetEventSlim _released = new(true);
    private volatile string? _gatedValue;

    private readonly ManualResetEventSlim _inboundReached = new(false);
    private readonly ManualResetEventSlim _inboundReleased = new(true);
    private volatile string? _gatedInboundValue;

    public void GateWritesOf(string value)
    {
        _reached.Reset();
        _released.Reset();
        _gatedValue = value;
    }

    public void WaitUntilGated() => _reached.Wait(TimeSpan.FromSeconds(30));

    public void Release()
    {
        _gatedValue = null;
        _released.Set();
    }

    /// <summary>
    /// Holds an inbound value inside the conversion a read-back runs before it applies. That is the
    /// window between the guards ranking the read-back against local state and the apply itself, so a
    /// test can commit a local write neither guard could have seen.
    /// </summary>
    public void GateInboundOf(string value)
    {
        _inboundReached.Reset();
        _inboundReleased.Reset();
        _gatedInboundValue = value;
    }

    public void WaitUntilInboundGated() => _inboundReached.Wait(TimeSpan.FromSeconds(30));

    public void ReleaseInbound()
    {
        _gatedInboundValue = null;
        _inboundReleased.Set();
    }

    public override object? ConvertToNodeValue(object? propertyValue, RegisteredSubjectProperty property)
    {
        if (_gatedValue is { } gated && Equals(propertyValue, gated))
        {
            _reached.Set();
            _released.Wait(TimeSpan.FromSeconds(30));
        }

        return base.ConvertToNodeValue(propertyValue, property);
    }

    public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property)
    {
        if (_gatedInboundValue is { } gated && Equals(nodeValue, gated))
        {
            _inboundReached.Set();
            _inboundReleased.Wait(TimeSpan.FromSeconds(30));
        }

        return base.ConvertToPropertyValue(nodeValue, property);
    }

    public void Dispose()
    {
        Release();
        ReleaseInbound();
        _reached.Dispose();
        _released.Dispose();
        _inboundReached.Dispose();
        _inboundReleased.Dispose();
    }
}

/// <summary>
/// A client connected to a server whose Trigger node behaves like a PLC that consumes a command: it
/// accepts every write and keeps <see cref="ServerValue"/> as the node's value. The write is therefore
/// a no-op for the server's model, and this server dates a node from the model rather than from the
/// write, so the read-back comes back carrying a timestamp the client's own write never moved. That is
/// the case a timestamp comparison on its own cannot rank, which is what this fixture is built to set
/// up, and it holds whether or not the client sends a SourceTimestamp.
/// </summary>
internal sealed class ReadAfterWriteFixture : IAsyncDisposable
{
    /// <summary>The value the Trigger node holds at all times, whatever a client writes to it.</summary>
    public const string ServerValue = "server-value";

    /// <summary>
    /// What the server revises the requested SamplingInterval=0 to, which is both what arms the
    /// read-after-write and how long after a write the read-back is scheduled.
    /// </summary>
    private const int RevisedSamplingIntervalMilliseconds = 200;

    /// <summary>
    /// Outbound changes leave in ticks of this period, so a local write made just after one flush is
    /// still unsent a read-back later, which is what keeps it from coalescing the pending read away.
    /// </summary>
    private static readonly TimeSpan OutboundFlushPeriod = TimeSpan.FromSeconds(3);

    /// <summary>Added to the revised interval before the read-back runs.</summary>
    private static readonly TimeSpan ReadAfterWriteBuffer = TimeSpan.FromSeconds(1);

    private readonly PortLease _port;
    private readonly OpcUaTestServer<ReadAfterWriteRoot> _server;
    private readonly OpcUaTestClient<ReadAfterWriteRoot> _client;
    private readonly GatedValueConverter _converter;
    private readonly BaseDataVariableState _triggerNode;

    private ReadAfterWriteFixture(
        PortLease port,
        OpcUaTestServer<ReadAfterWriteRoot> server,
        OpcUaTestClient<ReadAfterWriteRoot> client,
        GatedValueConverter converter,
        BaseDataVariableState triggerNode)
    {
        _port = port;
        _server = server;
        _client = client;
        _converter = converter;
        _triggerNode = triggerNode;
    }

    public ReadAfterWriteChild ClientChild => _client.Root!.Child!;

    private ReadAfterWriteMetrics Metrics =>
        ((OpcUaSubjectClientSource)_client.Source!).ReadAfterWriteMetrics;

    public static async Task<ReadAfterWriteFixture> StartAsync(ITestOutputHelper output)
    {
        var logger = new TestLogger(output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        var server = new OpcUaTestServer<ReadAfterWriteRoot>(logger);
        var converter = new GatedValueConverter();
        OpcUaTestClient<ReadAfterWriteRoot>? client = null;
        try
        {
            await server.StartAsync(
                createRoot: context => new ReadAfterWriteRoot(context),
                initializeDefaults: (context, root) =>
                    root.Child = new ReadAfterWriteChild(context) { Trigger = ServerValue },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            var serverProperty = new PropertyReference(server.Root!.Child!, nameof(ReadAfterWriteChild.Trigger));
            var refusedProperty = new PropertyReference(server.Root!.Child!, nameof(ReadAfterWriteChild.Refused));
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Server!.TryGetVariableNode(serverProperty, out _) &&
                      server.Server!.TryGetVariableNode(refusedProperty, out _),
                message: "the Trigger and Refused variable nodes should exist");

            server.Server!.TryGetVariableNode(serverProperty, out var triggerNode);
            server.Server!.TryGetVariableNode(refusedProperty, out var refusedNode);
            ConfigureTriggerNode(server.Server!, triggerNode!);
            ConfigureRefusedNode(server.Server!, refusedNode!);

            client = new OpcUaTestClient<ReadAfterWriteRoot>(logger, configuration =>
            {
                configuration.ValueConverter = converter;
                configuration.BufferTime = OutboundFlushPeriod;
                configuration.ReadAfterWriteBuffer = ReadAfterWriteBuffer;
            });

            await client.StartAsync(
                createRoot: context => new ReadAfterWriteRoot(context),
                isConnected: root => root.Child?.Trigger == ServerValue,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            return new ReadAfterWriteFixture(port, server, client, converter, triggerNode!);
        }
        catch
        {
            // A leaked lease would starve the port pool's parallel slots for the rest of the run.
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            await server.DisposeAsync();
            converter.Dispose();
            port.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gives the node a minimum sampling interval, which is what makes the server revise the requested
    /// SamplingInterval=0 upwards and therefore what arms the read-after-write, and makes every write to
    /// it a no-op that leaves <see cref="ServerValue"/> in place.
    /// </summary>
    private static void ConfigureTriggerNode(IOpcUaSubjectServer server, BaseDataVariableState node)
    {
        var standardServer = (OpcUaStandardServer)server.CurrentServer!;
        lock (standardServer.NodeManagerLock!)
        {
            node.MinimumSamplingInterval = RevisedSamplingIntervalMilliseconds;
            node.OnWriteValue = (ISystemContext _, NodeState _, NumericRange _, QualifiedName _,
                ref object value, ref StatusCode _, ref DateTime _) =>
            {
                value = ServerValue;
                return ServiceResult.Good;
            };

            node.ClearChangeMasks(standardServer.CurrentInstance.DefaultSystemContext, false);
        }
    }

    /// <summary>
    /// Arms the node for read-after-writes the same way, but makes it refuse every write, so a batch
    /// carrying it and <see cref="ReadAfterWriteChild.Trigger"/> comes back a partial failure.
    /// </summary>
    private static void ConfigureRefusedNode(IOpcUaSubjectServer server, BaseDataVariableState node)
    {
        var standardServer = (OpcUaStandardServer)server.CurrentServer!;
        lock (standardServer.NodeManagerLock!)
        {
            node.MinimumSamplingInterval = RevisedSamplingIntervalMilliseconds;
            node.OnWriteValue = (ISystemContext _, NodeState _, NumericRange _, QualifiedName _,
                ref object _, ref StatusCode _, ref DateTime _) => StatusCodes.BadUserAccessDenied;

            node.ClearChangeMasks(standardServer.CurrentInstance.DefaultSystemContext, false);
        }
    }

    /// <summary>
    /// Drives the node directly, so a test can produce an inbound value with a chosen status and source
    /// timestamp. Only a status change reaches the client: the Status trigger discards value-only ones.
    /// </summary>
    public void PublishToNode(object? value, StatusCode statusCode, DateTime sourceTimestamp)
    {
        var standardServer = (OpcUaStandardServer)_server.Server!.CurrentServer!;
        lock (standardServer.NodeManagerLock!)
        {
            // Value before status: the SDK's setter resets an untouched node's status to Good.
            _triggerNode.Value = value;
            _triggerNode.StatusCode = statusCode;
            _triggerNode.Timestamp = sourceTimestamp;
            _triggerNode.ClearChangeMasks(standardServer.CurrentInstance.DefaultSystemContext, false);
        }
    }

    public void GateOutboundWriteOf(string value) => _converter.GateWritesOf(value);

    public void WaitUntilOutboundWriteIsGated() => _converter.WaitUntilGated();

    public void ReleaseOutboundWrite() => _converter.Release();

    public void GateInboundValueOf(string value) => _converter.GateInboundOf(value);

    public void WaitUntilInboundValueIsGated() => _converter.WaitUntilInboundGated();

    public void ReleaseInboundValue() => _converter.ReleaseInbound();

    /// <summary>How many read-backs have been scheduled, which only successful writes may add to.</summary>
    public long ScheduledReadBackCount => Metrics.Scheduled;

    public Task WaitForScheduledReadBackAsync() =>
        AsyncTestHelpers.WaitUntilAsync(
            () => Metrics.Scheduled >= 1,
            message: "the write should have been acknowledged and a read-back scheduled");

    public Task WaitForSkippedReadBackAsync() =>
        AsyncTestHelpers.WaitUntilAsync(
            () => Metrics.Skipped >= 1,
            message: "the read-back should have run and discarded its value as superseded");

    public Task WaitForAppliedReadBackAsync() =>
        AsyncTestHelpers.WaitUntilAsync(
            () => Metrics.Executed >= 1,
            message: "the read-back should have run and applied the server's value");

    public async ValueTask DisposeAsync()
    {
        // Before the client stops, so a gated flush is not still parked inside the converter.
        _converter.Release();

        await _client.DisposeAsync();
        await _server.DisposeAsync();
        _converter.Dispose();
        _port.Dispose();
    }
}

using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// What the server counts and applies as inbound. Only a client's write is: it is applied inside the
/// node's own write and counted there, once. Every other route onto a node, the flush loop's own
/// assignment and the flush a node removal performs, must reach neither the subject nor the counter.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaServerSelfWriteTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaServerSelfWriteTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheServerWritesItsOwnNodes_ThenNothingIsAppliedBackToTheSubject()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<SelfWriteTestRoot>(logger);
        await server.StartAsync(
            createRoot: context => new SelfWriteTestRoot(context),
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var serverService = server.Server!;
        var property = new PropertyReference(server.Root!, nameof(SelfWriteTestRoot.Value));

        // Act: a purely local write. No client ever connects, so every inbound apply the server
        // records can only be its own write coming back.
        server.Root!.Value = "written by the server";

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverService.TryGetVariableNode(property, out var node) &&
                  Equals(node.Value, "written by the server"),
            message: "the server should push the local write onto its node");

        // Assert: the write reached the node without being counted as traffic from a client.
        // Throughput.IncomingPerSecond counts what flows into the subject tree, and no client exists.
        Assert.Equal(0d, serverService.Diagnostics.Throughput.IncomingPerSecond);
    }

    /// <summary>
    /// Removing a node ORs in Deleted and flushes it, which is the one flush of a node the connector
    /// performs without having anything to say about the property behind it.
    /// </summary>
    [Fact]
    public async Task WhenASubjectIsDetached_ThenNothingIsAppliedBackToTheSubject()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<SelfWriteTestParent>(logger);
        await server.StartAsync(
            createRoot: context => new SelfWriteTestParent(context),
            initializeDefaults: (context, root) =>
                root.Child = new SelfWriteTestChild(context) { Value = "initial" },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        var serverService = server.Server!;
        var property = new PropertyReference(server.Root!.Child!, nameof(SelfWriteTestChild.Value));

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverService.TryGetVariableNode(property, out _),
            message: "the child's variable node should exist");

        Assert.Equal(0d, serverService.Diagnostics.Throughput.IncomingPerSecond);

        // Act: detaching runs the removal synchronously on this thread.
        server.Root.Child = null;

        // Assert
        Assert.Equal(0d, serverService.Diagnostics.Throughput.IncomingPerSecond);
    }

    [Fact]
    public async Task WhenAClientWritesOnce_ThenTheIncomingCounterCountsItOnce()
    {
        // Arrange
        await using var fixture = await WriteIntegrityFixture.StartAsync(_output);
        var nodeId = fixture.NodeId(nameof(WriteIntegrityChild.Value));

        // Act
        var statusCode = await fixture.Session.WriteAsync(nodeId, "counted");

        // Assert: one write is one unit of inbound traffic. A second path onto the same value inflates
        // every rate the diagnostics report, and nothing else in the counter would show it.
        Assert.True(StatusCode.IsGood(statusCode), $"The write must not be answered with '{statusCode}'.");
        Assert.Equal(1d, fixture.Server.Diagnostics.Throughput.IncomingPerSecond!.Value * 60d, precision: 6);
    }
}

[InterceptorSubject]
public partial class SelfWriteTestRoot
{
    [Path("opc", "Value")]
    public partial string? Value { get; set; }
}

[InterceptorSubject]
public partial class SelfWriteTestParent
{
    [Path("opc", "Child")]
    [OpcUaReference("HasComponent")]
    public partial SelfWriteTestChild? Child { get; set; }
}

[InterceptorSubject]
public partial class SelfWriteTestChild
{
    [Path("opc", "Value")]
    public partial string? Value { get; set; }
}

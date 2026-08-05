using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// The server must never apply its own node state back to the subject. Two paths reach the
/// <c>StateChanged</c> handler that carries client writes: the flush loop's own <c>ClearChangeMasks</c>,
/// and node removal flushing the value mask set at creation. Both are masked by the equality check until
/// the subject has moved on, at which point they overwrite the newer commit with an older value.
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
        // IncomingChangesPerSecond is documented as "client writes to server", and no client exists.
        Assert.Equal(0d, serverService.Diagnostics.IncomingChangesPerSecond);
    }

    /// <summary>
    /// The value mask set when a variable node is created is never cleared, so removing the node ORs in
    /// Deleted and flushes both, which reaches the same handler carrying the node's creation value.
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

        Assert.Equal(0d, serverService.Diagnostics.IncomingChangesPerSecond);

        // Act: detaching runs the removal synchronously on this thread.
        server.Root.Child = null;

        // Assert
        Assert.Equal(0d, serverService.Diagnostics.IncomingChangesPerSecond);
    }

    /// <summary>
    /// Node removal flushes whatever masks are pending and, unguarded, would do so from the detaching
    /// thread. This reproduces the write loop's state between assigning a node value and flushing it
    /// itself: the lock is held and a value mask is pending. A detach must not be able to flush that.
    ///
    /// The wait cannot make this flaky. While this thread holds the lock, correct code cannot reach the
    /// flush at all, so the assertions hold however long the detach is given; a longer wait only makes
    /// the failing case easier to catch. Verified in the other direction by removing the lock, which
    /// fails the test.
    /// </summary>
    [Fact]
    public async Task WhenASubjectIsDetachedWhileTheWriteLoopHoldsTheLock_ThenNothingIsAppliedBackToTheSubject()
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
        var child = server.Root!.Child!;
        var property = new PropertyReference(child, nameof(SelfWriteTestChild.Value));

        await AsyncTestHelpers.WaitUntilAsync(
            () => serverService.TryGetVariableNode(property, out _),
            message: "the child's variable node should exist");

        Assert.True(serverService.TryGetVariableNode(property, out var node));
        var nodeManagerLock = ((OpcUaStandardServer)serverService.CurrentServer!).NodeManagerLock;
        Assert.NotNull(nodeManagerLock);

        Assert.Equal(0d, serverService.Diagnostics.IncomingChangesPerSecond);

        lock (nodeManagerLock)
        {
            // The write loop's mid-write state: value assigned, its own flush not yet reached.
            node!.Value = "stale write loop value";

            var detach = new Thread(() => server.Root.Child = null) { IsBackground = true };
            detach.Start();

            // A window for the damage to land, not a synchronisation point. The removal blocks inside
            // DeleteNode either way, because it takes this lock itself before it finishes; what the fix
            // changes is that it can no longer flush the mask on the way there.
            detach.Join(TimeSpan.FromSeconds(5));

            // Assert while still holding the lock: releasing it lets the removal flush the mask this
            // test left pending, which is the one case the handler's comment calls out.
            Assert.Equal(0d, serverService.Diagnostics.IncomingChangesPerSecond);
            Assert.Equal("initial", child.Value);
        }

        await AsyncTestHelpers.WaitUntilAsync(() => server.Root.Child is null);
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

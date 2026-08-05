using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// The server pushes a local change onto its node and then calls <c>ClearChangeMasks</c>, which raises
/// <c>StateChanged</c> synchronously on the same thread. That handler is how a client write reaches the
/// subject, so without a guard the server also applies its own writes back to the subject as if a client
/// had sent them.
///
/// Normally invisible, because the value it applies is the one just written and the equality check drops
/// it. It stops being invisible when the subject moved on between the batch being assembled and the node
/// write: the reflection then carries the older value and overwrites the newer commit.
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
}

[InterceptorSubject]
public partial class SelfWriteTestRoot
{
    [Path("opc", "Value")]
    public partial string? Value { get; set; }
}

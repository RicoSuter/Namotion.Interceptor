using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Round-trip test for subject identity preservation across OPC UA server + client.
///
/// When the C# model on the server contains the same subject instance reachable from
/// two different parents (a DAG), the server publishes it as a single NodeId with one
/// reference from each parent. The client browses each parent independently, hits the
/// same NodeId, and resolves both parent properties to the same C# instance via
/// <c>subjectsByNodeId</c>. This locks in the round-trip invariant: shared instances
/// on the server stay shared on the client.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaSubjectReuseTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaSubjectReuseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenSameSubjectReachableFromTwoParents_ThenClientResolvesBothToSameInstance()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<SubjectReuseTestRoot>(logger);
        await server.StartAsync(
            createRoot: context => new SubjectReuseTestRoot(context),
            initializeDefaults: (context, root) =>
            {
                var shared = new SharedReuseItem(context) { Name = "shared" };
                root.GroupA = new SubjectReuseGroup(context) { Shared = shared };
                root.GroupB = new SubjectReuseGroup(context) { Shared = shared };
                root.Connected = true;
            },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        // Server side sanity check: the model itself shares the instance across both groups.
        Assert.NotNull(server.Root!.GroupA?.Shared);
        Assert.Same(server.Root.GroupA!.Shared, server.Root.GroupB!.Shared);

        await using var client = new OpcUaTestClient<SubjectReuseTestRoot>(logger);
        await client.StartAsync(
            createRoot: context => new SubjectReuseTestRoot(context),
            isConnected: root => root.Connected,
            serverUrl: port.ServerUrl,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.GroupA?.Shared is not null
                  && client.Root.GroupB?.Shared is not null,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client did not materialize Shared subject under both groups");

        // Act + Assert: round-trip preserves subject identity.
        // The server published the shared instance as a single NodeId reachable from both
        // GroupA and GroupB; the client's subjectsByNodeId cache resolves both browse paths
        // to the same C# instance.
        Assert.Same(client.Root!.GroupA!.Shared, client.Root.GroupB!.Shared);
        Assert.Equal("shared", client.Root.GroupA.Shared!.Name);
    }

    [Fact]
    public async Task WhenPropertyHoldsSameSubjectAtSeveralPlaces_ThenServerPublishesItOnceAtItsFirstPlace()
    {
        // Arrange
        var serverLog = new SharedLogBuffer();
        using var port = await OpcUaTestPortPool.AcquireAsync();

        SharedReuseItem? shared = null;
        SharedReuseItem? sharedByKey = null;

        await using var server = new OpcUaTestServer<SubjectReuseTestRoot>(new TestLogger(serverLog));

        // Act
        await server.StartAsync(
            createRoot: context => new SubjectReuseTestRoot(context),
            initializeDefaults: (context, root) =>
            {
                shared = new SharedReuseItem(context) { Name = "shared" };
                root.Items = [shared, new SharedReuseItem(context) { Name = "other" }, shared];

                sharedByKey = new SharedReuseItem(context) { Name = "shared" };
                root.ItemsByKey = new Dictionary<string, SharedReuseItem>
                {
                    ["a"] = sharedByKey,
                    ["b"] = new SharedReuseItem(context) { Name = "other" },
                    ["c"] = sharedByKey
                };
            },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        // Assert
        var serverService = (OpcUaSubjectServer)server.Server!;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverService.TryGetVariableNode(new PropertyReference(shared!, nameof(SharedReuseItem.Name)), out _)
                  && serverService.TryGetVariableNode(new PropertyReference(sharedByKey!, nameof(SharedReuseItem.Name)), out _),
            message: "the repeated subjects' variable nodes should exist");

        var systemContext = ((OpcUaStandardServer)serverService.CurrentServer!).CurrentInstance.DefaultSystemContext;
        AssertPublishedOnceAtFirstPlace(serverService, systemContext, shared!, "Items[0]");
        AssertPublishedOnceAtFirstPlace(serverService, systemContext, sharedByKey!, "a");

        Assert.DoesNotContain(serverLog.ReadFrom(0), line => line.Contains("reused at 'SubjectReuseTestRoot."));
    }

    private static void AssertPublishedOnceAtFirstPlace(
        OpcUaSubjectServer serverService, ISystemContext systemContext, SharedReuseItem subject, string expectedBrowseName)
    {
        Assert.True(serverService.TryGetVariableNode(new PropertyReference(subject, nameof(SharedReuseItem.Name)), out var nameNode));
        var subjectNode = (BaseInstanceState)nameNode!.Parent!;
        var folderNode = subjectNode.Parent!;

        Assert.Equal(expectedBrowseName, subjectNode.BrowseName.Name);

        // GetReferences returns the references that are not children, so a node is reached once
        // when it is either one child or one reference of its folder.
        var children = new List<BaseInstanceState>();
        folderNode.GetChildren(systemContext, children);
        var references = new List<IReference>();
        folderNode.GetReferences(systemContext, references);

        var childCount = children.Count(child => child.NodeId == subjectNode.NodeId);
        var referenceCount = references.Count(reference => !reference.IsInverse && reference.TargetId == subjectNode.NodeId);
        Assert.Equal(1, childCount + referenceCount);
    }
}

[InterceptorSubject]
public partial class SubjectReuseTestRoot
{
    public SubjectReuseTestRoot()
    {
        Items = [];
    }

    [Path("opc", "Connected")]
    public partial bool Connected { get; set; }

    [Path("opc", "GroupA")]
    [OpcUaReference("HasComponent")]
    public partial SubjectReuseGroup? GroupA { get; set; }

    [Path("opc", "GroupB")]
    [OpcUaReference("HasComponent")]
    public partial SubjectReuseGroup? GroupB { get; set; }

    [Path("opc", "Items")]
    public partial SharedReuseItem[] Items { get; set; }

    [Path("opc", "ItemsByKey")]
    public partial Dictionary<string, SharedReuseItem>? ItemsByKey { get; set; }
}

[InterceptorSubject]
public partial class SubjectReuseGroup
{
    [Path("opc", "Shared")]
    [OpcUaReference("HasComponent")]
    public partial SharedReuseItem? Shared { get; set; }
}

[InterceptorSubject]
public partial class SharedReuseItem
{
    [Path("opc", "Name")]
    public partial string Name { get; set; }
}

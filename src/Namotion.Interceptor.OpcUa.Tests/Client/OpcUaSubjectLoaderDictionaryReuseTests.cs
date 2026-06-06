using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderDictionaryReuseTests : OpcUaSubjectLoaderTestsBase
{
    [Fact]
    public async Task WhenDictionaryHasNonStringKeys_ThenExistingChildrenAreReusedDuringLoad()
    {
        // Arrange: a statically modelled IReadOnlyDictionary<int, ...> is pre-populated before the load.
        // The browse tree exposes the same two entries (Items[1], Items[2]), so the load must rebind the
        // existing child instances instead of recreating them. The reuse lookup keys children by the
        // dictionary's converted key (int), so it must convert the browse-name key ("1"/"2") the same way.
        var rootId = new NodeId(1, 0);
        var itemsId = new NodeId(100, 2);
        var item1Id = new NodeId(101, 2);
        var item2Id = new NodeId(102, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] = [CreateObjectReferenceDescription("Items", new ExpandedNodeId(itemsId))],
            [itemsId] =
            [
                CreateObjectReferenceDescription("Items[1]", new ExpandedNodeId(item1Id)),
                CreateObjectReferenceDescription("Items[2]", new ExpandedNodeId(item2Id))
            ],
            [item1Id] = [],
            [item2Id] = []
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);

        var (loader, _) = CreateLoader();

        var modelContext = InterceptorSubjectContext.Create().WithRegistry();
        var container = new DictionaryReuseContainer(modelContext);
        new LifecycleInterceptor().AttachSubjectToContext(container);

        var itemOne = new DictionaryReuseItem(modelContext) { Name = "one" };
        var itemTwo = new DictionaryReuseItem(modelContext) { Name = "two" };
        container.Items = new Dictionary<int, DictionaryReuseItem> { [1] = itemOne, [2] = itemTwo };

        var rootNode = CreateObjectReferenceDescription("Root", new ExpandedNodeId(rootId));

        // Act
        await loader.LoadSubjectAsync(container, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.NotNull(container.Items);
        Assert.Equal(2, container.Items.Count);
        Assert.Same(itemOne, container.Items[1]);
        Assert.Same(itemTwo, container.Items[2]);
    }
}

[InterceptorSubject]
public partial class DictionaryReuseContainer
{
    [OpcUaNode("Items")]
    public partial IReadOnlyDictionary<int, DictionaryReuseItem> Items { get; set; }
}

[InterceptorSubject]
public partial class DictionaryReuseItem
{
    public partial string Name { get; set; }
}

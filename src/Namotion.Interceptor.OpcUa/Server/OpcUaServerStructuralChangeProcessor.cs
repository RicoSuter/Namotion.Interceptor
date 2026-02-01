using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Processes structural property changes (add/remove subjects) for OPC UA server.
/// Creates or removes nodes in the OPC UA address space when the C# model changes.
/// Note: Source filtering (loop prevention) is handled by ChangeQueueProcessor, not here.
/// </summary>
internal class OpcUaServerStructuralChangeProcessor : StructuralChangeProcessor
{
    private readonly CustomNodeManager _nodeManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpcUaServerStructuralChangeProcessor"/> class.
    /// </summary>
    /// <param name="nodeManager">The custom node manager for creating/removing nodes.</param>
    public OpcUaServerStructuralChangeProcessor(CustomNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
    }

    /// <inheritdoc />
    protected override Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
    {
        _nodeManager.CreateSubjectNode(property, subject, index);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task OnSubjectRemovedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
    {
        _nodeManager.RemoveSubjectNodes(subject, property);

        // Re-index collection BrowseNames after removal to maintain contiguous indices
        // This ensures BrowseNames like "People[0]", "People[1]" remain sequential
        if (property.IsSubjectCollection)
        {
            _nodeManager.ReindexCollectionBrowseNames(property);
        }

        return Task.CompletedTask;
    }
}

using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Processes structural property changes (add/remove subjects) for OPC UA client.
/// Creates or removes MonitoredItems when the C# model changes.
/// Optionally calls AddNodes/DeleteNodes on the server when EnableRemoteNodeManagement is enabled.
/// Note: Source filtering (loop prevention) is handled by ChangeQueueProcessor, not here.
/// </summary>
internal class OpcUaClientStructuralChangeProcessor : StructuralChangeProcessor
{
    private readonly OpcUaSubjectClientSource _source;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpcUaClientStructuralChangeProcessor"/> class.
    /// </summary>
    /// <param name="source">The OPC UA client source for tracking subjects.</param>
    /// <param name="configuration">The client configuration.</param>
    /// <param name="subjectLoader">The subject loader for loading new subjects.</param>
    /// <param name="logger">The logger.</param>
    public OpcUaClientStructuralChangeProcessor(
        OpcUaSubjectClientSource source,
        OpcUaClientConfiguration configuration,
        OpcUaSubjectLoader subjectLoader,
        ILogger logger)
    {
        _source = source;
        _configuration = configuration;
        _subjectLoader = subjectLoader;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the current OPC UA session. Must be set before processing changes.
    /// </summary>
    internal ISession? CurrentSession { get; set; }

    /// <inheritdoc />
    protected override async Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
    {
        var session = CurrentSession;
        if (session is null)
        {
            _logger.LogWarning(
                "Cannot create MonitoredItems for added subject on property '{PropertyName}': no active session.",
                property.Name);
            return;
        }

        // Check if subject is already tracked (shared subject scenario)
        if (_source.IsSubjectTracked(subject))
        {
            // Subject already tracked - just increment reference count
            if (_source.TryGetSubjectNodeId(subject, out var existingNodeId) && existingNodeId is not null)
            {
                _source.TrackSubject(subject, existingNodeId, () => []);
                _logger.LogDebug(
                    "Subject already tracked for property '{PropertyName}', incremented reference count.",
                    property.Name);
            }
            return;
        }

        // Determine the parent NodeId for browsing
        NodeId? parentNodeId = null;
        var parentSubject = property.Parent.Subject;

        if (_source.TryGetSubjectNodeId(parentSubject, out var parentNode) && parentNode is not null)
        {
            parentNodeId = parentNode;
        }
        else if (ReferenceEquals(parentSubject, _source.RootSubject))
        {
            // Parent is root - browse from ObjectsFolder or RootName
            parentNodeId = _configuration.RootName is not null
                ? await TryFindRootNodeIdAsync(session, CancellationToken.None).ConfigureAwait(false)
                : ObjectIds.ObjectsFolder;
        }

        if (parentNodeId is null)
        {
            _logger.LogWarning(
                "Cannot create MonitoredItems for added subject on property '{PropertyName}': parent node not found.",
                property.Name);
            return;
        }

        // Find the child node by browsing
        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
        if (propertyName is null)
        {
            return;
        }

        var childNodeRef = await TryFindChildNodeAsync(session, parentNodeId, propertyName, index, property.IsSubjectCollection, CancellationToken.None).ConfigureAwait(false);
        if (childNodeRef is null)
        {
            _logger.LogDebug(
                "Child node not found for property '{PropertyName}' with index '{Index}'. Subject may be created locally only.",
                property.Name, index);
            return;
        }

        // Load the subject and create MonitoredItems
        var monitoredItems = await _subjectLoader.LoadSubjectAsync(subject, childNodeRef, session, CancellationToken.None).ConfigureAwait(false);

        if (monitoredItems.Count > 0)
        {
            // Add monitored items to subscriptions
            var sessionManager = _source.SessionManager;
            if (sessionManager is not null)
            {
                await sessionManager.AddMonitoredItemsAsync(monitoredItems, session, CancellationToken.None).ConfigureAwait(false);
            }

            _logger.LogDebug(
                "Created {Count} MonitoredItems for added subject on property '{PropertyName}'.",
                monitoredItems.Count, property.Name);
        }
    }

    /// <inheritdoc />
    protected override Task OnSubjectRemovedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
    {
        // The SourceOwnershipManager.OnSubjectDetaching callback handles the actual removal
        // of MonitoredItems when the subject is detached from the context.
        // This is triggered by the lifecycle interceptor when the subject loses all references.

        // For explicit removal tracking, we could add additional logic here if needed.
        _logger.LogDebug(
            "Subject removed from property '{PropertyName}' at index '{Index}'. " +
            "MonitoredItems will be cleaned up when subject is detached from context.",
            property.Name, index);

        return Task.CompletedTask;
    }

    private async Task<NodeId?> TryFindRootNodeIdAsync(ISession session, CancellationToken cancellationToken)
    {
        if (_configuration.RootName is null)
        {
            return ObjectIds.ObjectsFolder;
        }

        var references = await BrowseNodeAsync(session, ObjectIds.ObjectsFolder, cancellationToken).ConfigureAwait(false);
        foreach (var reference in references)
        {
            if (reference.BrowseName.Name == _configuration.RootName)
            {
                return ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            }
        }

        return null;
    }

    private async Task<ReferenceDescription?> TryFindChildNodeAsync(
        ISession session,
        NodeId parentNodeId,
        string propertyName,
        object? index,
        bool isCollection,
        CancellationToken cancellationToken)
    {
        var references = await BrowseNodeAsync(session, parentNodeId, cancellationToken).ConfigureAwait(false);

        // For collections/dictionaries, first find the container node
        if (isCollection || index is not null)
        {
            // Find container first
            NodeId? containerNodeId = null;
            foreach (var reference in references)
            {
                if (reference.BrowseName.Name == propertyName)
                {
                    containerNodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
                    break;
                }
            }

            if (containerNodeId is null)
            {
                return null;
            }

            // Browse container for specific item
            var containerChildren = await BrowseNodeAsync(session, containerNodeId, cancellationToken).ConfigureAwait(false);
            var expectedBrowseName = isCollection ? $"{propertyName}[{index}]" : index?.ToString();

            foreach (var child in containerChildren)
            {
                if (child.BrowseName.Name == expectedBrowseName)
                {
                    return child;
                }
            }

            return null;
        }

        // For single references, find direct child
        foreach (var reference in references)
        {
            if (reference.BrowseName.Name == propertyName)
            {
                return reference;
            }
        }

        return null;
    }

    private static async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        ISession session,
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        const uint nodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

        var browseDescription = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = nodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = nodeClassMask,
                ResultMask = (uint)BrowseResultMask.All
            }
        };

        var response = await session.BrowseAsync(
            null,
            null,
            0,
            browseDescription,
            cancellationToken).ConfigureAwait(false);

        if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
        {
            return response.Results[0].References;
        }

        return [];
    }
}

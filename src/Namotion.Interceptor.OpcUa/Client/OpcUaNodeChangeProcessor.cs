using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Processes OPC UA node changes (from ModelChangeEvents or periodic resync) to update the C# model.
/// Compares remote address space with local model and creates/removes subjects as needed.
/// </summary>
internal class OpcUaNodeChangeProcessor
{
    private readonly OpcUaSubjectClientSource _source;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpcUaNodeChangeProcessor"/> class.
    /// </summary>
    /// <param name="source">The OPC UA client source.</param>
    /// <param name="configuration">The client configuration.</param>
    /// <param name="subjectLoader">The subject loader for creating new subjects.</param>
    /// <param name="logger">The logger.</param>
    public OpcUaNodeChangeProcessor(
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
    /// Processes node changes for a collection property by comparing remote nodes with local model.
    /// </summary>
    /// <param name="property">The collection property to sync.</param>
    /// <param name="containerNodeId">The NodeId of the container (folder) node in OPC UA.</param>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ProcessCollectionNodeChangesAsync(
        RegisteredSubjectProperty property,
        NodeId containerNodeId,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (!property.IsSubjectCollection)
        {
            return;
        }

        // Browse remote nodes
        var remoteChildren = await BrowseNodeAsync(session, containerNodeId, cancellationToken).ConfigureAwait(false);

        // Get current local children
        var localChildren = property.Children.ToList();

        // Parse remote child indices (expecting pattern "PropertyName[index]")
        var remoteByIndex = new Dictionary<int, ReferenceDescription>();
        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);

        foreach (var remoteChild in remoteChildren)
        {
            var browseName = remoteChild.BrowseName.Name;
            if (TryParseCollectionIndex(browseName, propertyName, out var index))
            {
                remoteByIndex[index] = remoteChild;
            }
        }

        // Find differences
        var remoteIndices = new HashSet<int>(remoteByIndex.Keys);
        var localIndices = new HashSet<int>(Enumerable.Range(0, localChildren.Count));

        var indicesToAdd = remoteIndices.Except(localIndices).OrderBy(i => i).ToList();
        var indicesToRemove = localIndices.Except(remoteIndices).OrderByDescending(i => i).ToList();

        // Process additions
        foreach (var index in indicesToAdd)
        {
            if (remoteByIndex.TryGetValue(index, out var remoteChild))
            {
                _logger.LogDebug(
                    "Remote node added at index {Index} for property '{PropertyName}'. Creating local subject.",
                    index, property.Name);

                // Create new subject
                var newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(
                    property, remoteChild, session, cancellationToken).ConfigureAwait(false);

                // Load the subject (creates MonitoredItems)
                var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                    newSubject, remoteChild, session, cancellationToken).ConfigureAwait(false);

                // Add to subscriptions
                if (monitoredItems.Count > 0)
                {
                    var sessionManager = _source.SessionManager;
                    if (sessionManager is not null)
                    {
                        await sessionManager.AddMonitoredItemsAsync(
                            monitoredItems, session, cancellationToken).ConfigureAwait(false);
                    }
                }

                // Update local collection
                // Note: This is a simplified approach - real implementation would need to
                // properly insert at the correct index and update the property
            }
        }

        // Process removals - log only, actual removal happens via lifecycle
        foreach (var index in indicesToRemove)
        {
            _logger.LogDebug(
                "Remote node removed at index {Index} for property '{PropertyName}'. Local subject will be cleaned up.",
                index, property.Name);
        }
    }

    /// <summary>
    /// Processes node changes for a dictionary property by comparing remote nodes with local model.
    /// </summary>
    /// <param name="property">The dictionary property to sync.</param>
    /// <param name="containerNodeId">The NodeId of the container (folder) node in OPC UA.</param>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ProcessDictionaryNodeChangesAsync(
        RegisteredSubjectProperty property,
        NodeId containerNodeId,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (!property.IsSubjectDictionary)
        {
            return;
        }

        // Browse remote nodes
        var remoteChildren = await BrowseNodeAsync(session, containerNodeId, cancellationToken).ConfigureAwait(false);

        // Get current local children
        var localChildren = property.Children.ToDictionary(c => c.Index?.ToString() ?? "", c => c.Subject);

        // Build remote key set (BrowseName = dictionary key)
        var remoteByKey = new Dictionary<string, ReferenceDescription>();
        foreach (var remoteChild in remoteChildren)
        {
            var key = remoteChild.BrowseName.Name;
            if (!string.IsNullOrEmpty(key))
            {
                remoteByKey[key] = remoteChild;
            }
        }

        // Find differences
        var remoteKeys = new HashSet<string>(remoteByKey.Keys);
        var localKeys = new HashSet<string>(localChildren.Keys);

        var keysToAdd = remoteKeys.Except(localKeys).ToList();
        var keysToRemove = localKeys.Except(remoteKeys).ToList();

        // Process additions
        foreach (var key in keysToAdd)
        {
            if (remoteByKey.TryGetValue(key, out var remoteChild))
            {
                _logger.LogDebug(
                    "Remote node added with key '{Key}' for property '{PropertyName}'. Creating local subject.",
                    key, property.Name);

                // Create new subject
                var newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(
                    property, remoteChild, session, cancellationToken).ConfigureAwait(false);

                // Load the subject
                var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                    newSubject, remoteChild, session, cancellationToken).ConfigureAwait(false);

                // Add to subscriptions
                if (monitoredItems.Count > 0)
                {
                    var sessionManager = _source.SessionManager;
                    if (sessionManager is not null)
                    {
                        await sessionManager.AddMonitoredItemsAsync(
                            monitoredItems, session, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        // Process removals - log only
        foreach (var key in keysToRemove)
        {
            _logger.LogDebug(
                "Remote node removed with key '{Key}' for property '{PropertyName}'. Local subject will be cleaned up.",
                key, property.Name);
        }
    }

    /// <summary>
    /// Processes a ModelChangeEvent from the server.
    /// </summary>
    /// <param name="changes">The model change data from the event.</param>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ProcessModelChangeEventAsync(
        IReadOnlyList<ModelChangeStructureDataType> changes,
        ISession session,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            var verb = (ModelChangeStructureVerbMask)change.Verb;
            var affectedNodeId = change.Affected;

            _logger.LogDebug(
                "Processing ModelChangeEvent: Verb={Verb}, AffectedNode={NodeId}",
                verb, affectedNodeId);

            if (verb.HasFlag(ModelChangeStructureVerbMask.NodeAdded))
            {
                // A node was added - we need to find which property it belongs to
                // and potentially create a new subject for it
                await ProcessNodeAddedAsync(affectedNodeId, session, cancellationToken).ConfigureAwait(false);
            }
            else if (verb.HasFlag(ModelChangeStructureVerbMask.NodeDeleted))
            {
                // A node was deleted - find and clean up the corresponding subject
                ProcessNodeDeleted(affectedNodeId);
            }
            else if (verb.HasFlag(ModelChangeStructureVerbMask.ReferenceAdded))
            {
                // A reference was added - might need to add to collection/dictionary
                await ProcessReferenceAddedAsync(affectedNodeId, session, cancellationToken).ConfigureAwait(false);
            }
            else if (verb.HasFlag(ModelChangeStructureVerbMask.ReferenceDeleted))
            {
                // A reference was deleted
                ProcessReferenceDeleted(affectedNodeId);
            }
        }
    }

    private async Task ProcessNodeAddedAsync(NodeId nodeId, ISession session, CancellationToken cancellationToken)
    {
        // Find parent node to determine which property this belongs to
        // This is a simplified implementation - full implementation would need to:
        // 1. Browse parent of the added node
        // 2. Match parent to a tracked subject
        // 3. Determine which property the new node belongs to
        // 4. Create appropriate subject and MonitoredItems

        _logger.LogDebug("NodeAdded event for {NodeId}. Full handling requires parent resolution.", nodeId);
        await Task.CompletedTask;
    }

    private void ProcessNodeDeleted(NodeId nodeId)
    {
        // Find subject associated with this NodeId and trigger cleanup
        // The lifecycle interceptor will handle the actual removal when the subject is detached

        _logger.LogDebug("NodeDeleted event for {NodeId}.", nodeId);
    }

    private async Task ProcessReferenceAddedAsync(NodeId nodeId, ISession session, CancellationToken cancellationToken)
    {
        _logger.LogDebug("ReferenceAdded event for {NodeId}.", nodeId);
        await Task.CompletedTask;
    }

    private void ProcessReferenceDeleted(NodeId nodeId)
    {
        _logger.LogDebug("ReferenceDeleted event for {NodeId}.", nodeId);
    }

    private static bool TryParseCollectionIndex(string browseName, string? propertyName, out int index)
    {
        index = -1;

        if (propertyName is null)
        {
            return false;
        }

        // Expected format: "PropertyName[index]"
        var prefix = propertyName + "[";
        if (!browseName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = "]";
        if (!browseName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var indexStr = browseName.Substring(prefix.Length, browseName.Length - prefix.Length - suffix.Length);
        return int.TryParse(indexStr, out index);
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

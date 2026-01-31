using System.Collections;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Graph;
using Namotion.Interceptor.Registry;
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
    //  TODO: Rename to OpcUaGraphChangeProcessor and move to /Client/Graph?
    
    private readonly OpcUaSubjectClientSource _source;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly ILogger _logger;

    // Maps NodeId to subject for reverse lookup when processing node deletions
    private readonly Dictionary<NodeId, IInterceptorSubject> _nodeIdToSubject = new();
    private readonly Lock _nodeIdToSubjectLock = new();

    // Track recently deleted NodeIds to prevent periodic resync from re-adding them
    // This is needed when EnableRemoteNodeManagement is true - the client is the source of truth
    private readonly Dictionary<NodeId, DateTime> _recentlyDeletedNodeIds = new();
    private static readonly TimeSpan RecentlyDeletedExpiry = TimeSpan.FromSeconds(30);

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
    /// Registers a subject with its NodeId for reverse lookup during node deletion events.
    /// </summary>
    /// <param name="subject">The subject to register.</param>
    /// <param name="nodeId">The NodeId associated with the subject.</param>
    public void RegisterSubjectNodeId(IInterceptorSubject subject, NodeId nodeId)
    {
        lock (_nodeIdToSubjectLock)
        {
            _nodeIdToSubject[nodeId] = subject;
        }
    }

    /// <summary>
    /// Unregisters a subject's NodeId mapping.
    /// </summary>
    /// <param name="nodeId">The NodeId to unregister.</param>
    public void UnregisterSubjectNodeId(NodeId nodeId)
    {
        lock (_nodeIdToSubjectLock)
        {
            _nodeIdToSubject.Remove(nodeId);
        }
    }

    /// <summary>
    /// Clears all NodeId to subject mappings.
    /// </summary>
    public void Clear()
    {
        lock (_nodeIdToSubjectLock)
        {
            _nodeIdToSubject.Clear();
            _recentlyDeletedNodeIds.Clear();
        }
    }

    /// <summary>
    /// Marks a NodeId as recently deleted to prevent periodic resync from re-adding it.
    /// </summary>
    /// <param name="nodeId">The NodeId that was deleted.</param>
    public void MarkRecentlyDeleted(NodeId nodeId)
    {
        lock (_nodeIdToSubjectLock)
        {
            _recentlyDeletedNodeIds[nodeId] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Checks if a NodeId was recently deleted by the client.
    /// Used by periodic resync to avoid re-adding items that the client intentionally removed.
    /// </summary>
    /// <param name="nodeId">The NodeId to check.</param>
    /// <returns>True if the NodeId was recently deleted, false otherwise.</returns>
    public bool WasRecentlyDeleted(NodeId nodeId)
    {
        lock (_nodeIdToSubjectLock)
        {
            // Clean up expired entries
            var now = DateTime.UtcNow;
            var expiredKeys = new List<NodeId>();
            foreach (var (key, deletedAt) in _recentlyDeletedNodeIds)
            {
                if (now - deletedAt > RecentlyDeletedExpiry)
                {
                    expiredKeys.Add(key);
                }
            }
            foreach (var key in expiredKeys)
            {
                _recentlyDeletedNodeIds.Remove(key);
            }

            return _recentlyDeletedNodeIds.ContainsKey(nodeId);
        }
    }

    /// <summary>
    /// Performs a full resync of all structural properties (collections and dictionaries) for all tracked subjects.
    /// This is used for periodic resync fallback when ModelChangeEvents are not available.
    /// </summary>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PerformFullResyncAsync(ISession session, CancellationToken cancellationToken)
    {
        foreach (var subject in _source.GetTrackedSubjects())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject is null ||
                !_source.TryGetSubjectNodeId(subject, out var subjectNodeId) ||
                subjectNodeId is null)
            {
                continue;
            }

            foreach (var property in registeredSubject.Properties)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
                if (propertyName is null)
                {
                    continue;
                }

                if (property.IsSubjectCollection)
                {
                    var containerNodeId = await OpcUaBrowseHelper.FindChildNodeIdAsync(session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
                    if (containerNodeId is not null)
                    {
                        await ProcessCollectionNodeChangesAsync(property, containerNodeId, session, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (property.IsSubjectDictionary)
                {
                    var containerNodeId = await OpcUaBrowseHelper.FindChildNodeIdAsync(session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
                    if (containerNodeId is not null)
                    {
                        await ProcessDictionaryNodeChangesAsync(property, containerNodeId, session, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (property.IsSubjectReference)
                {
                    await ProcessReferenceNodeChangesAsync(property, subjectNodeId, propertyName, session, cancellationToken).ConfigureAwait(false);
                }
            }
        }
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
        var remoteChildren = await OpcUaBrowseHelper.BrowseNodeAsync(session, containerNodeId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Browse '{Container}' returned {Count} children: {Names}",
            containerNodeId, remoteChildren.Count,
            string.Join(", ", remoteChildren.Select(c => c.BrowseName.Name)));

        // Get current local children
        var localChildren = property.Children.ToList();

        // Parse remote child indices (expecting pattern "PropertyName[index]")
        var remoteByIndex = new Dictionary<int, ReferenceDescription>();
        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);

        foreach (var remoteChild in remoteChildren)
        {
            var browseName = remoteChild.BrowseName.Name;
            if (OpcUaBrowseHelper.TryParseCollectionIndex(browseName, propertyName, out var index))
            {
                remoteByIndex[index] = remoteChild;
            }
        }

        // Find differences
        var remoteIndices = new HashSet<int>(remoteByIndex.Keys);
        var localIndices = new HashSet<int>(Enumerable.Range(0, localChildren.Count));

        var indicesToAdd = remoteIndices.Except(localIndices).OrderBy(i => i).ToList();
        var indicesToRemove = localIndices.Except(remoteIndices).OrderByDescending(i => i).ToList();

        _logger.LogInformation("Collection sync for '{Property}': remote={Remote}, local={Local}, toAdd={Add}, toRemove={Remove}",
            property.Name, string.Join(",", remoteIndices), string.Join(",", localIndices),
            string.Join(",", indicesToAdd), string.Join(",", indicesToRemove));

        // When EnableRemoteNodeManagement is true and an item was recently deleted by the client,
        // skip re-adding it. The DeleteNodes call will eventually remove it from the server.
        if (_configuration.EnableRemoteNodeManagement && indicesToAdd.Count > 0)
        {
            var filteredIndicesToAdd = new List<int>();
            foreach (var index in indicesToAdd)
            {
                if (remoteByIndex.TryGetValue(index, out var remoteChild))
                {
                    var nodeId = ExpandedNodeId.ToNodeId(remoteChild.NodeId, session.NamespaceUris);
                    if (WasRecentlyDeleted(nodeId))
                    {
                        continue;
                    }
                }
                filteredIndicesToAdd.Add(index);
            }
            indicesToAdd = filteredIndicesToAdd;
        }

        // Process additions
        foreach (var index in indicesToAdd)
        {
            if (remoteByIndex.TryGetValue(index, out var remoteChild))
            {
                var newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(
                    property, remoteChild, session, cancellationToken).ConfigureAwait(false);

                var nodeId = ExpandedNodeId.ToNodeId(remoteChild.NodeId, session.NamespaceUris);
                RegisterSubjectNodeId(newSubject, nodeId);

                // Add to collection FIRST - this attaches the subject to the parent which registers it
                // The subject must be registered before LoadSubjectAsync can set up monitored items
                if (!SubjectPropertyHelper.AddToCollection(property, newSubject, _source))
                {
                    _logger.LogWarning(
                        "Cannot add to collection property '{PropertyName}': value is not an array.",
                        property.Name);
                    continue;
                }

                // Now load and set up monitoring - subject is now registered
                var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                    newSubject, remoteChild, session, cancellationToken).ConfigureAwait(false);

                if (monitoredItems.Count > 0)
                {
                    var sessionManager = _source.SessionManager;
                    if (sessionManager is not null)
                    {
                        await sessionManager.AddMonitoredItemsAsync(
                            monitoredItems, session, cancellationToken).ConfigureAwait(false);
                    }
                }

                await _source.ReadAndApplySubjectValuesAsync(newSubject, session, cancellationToken).ConfigureAwait(false);
            }
        }

        // Process removals
        if (indicesToRemove.Count > 0)
        {
            if (!SubjectPropertyHelper.RemoveFromCollectionByIndices(property, indicesToRemove, _source))
            {
                _logger.LogWarning(
                    "Could not remove {RemovedCount} subjects from collection property '{PropertyName}'.",
                    indicesToRemove.Count, property.Name);
            }
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
        var remoteChildren = await OpcUaBrowseHelper.BrowseNodeAsync(session, containerNodeId, cancellationToken).ConfigureAwait(false);

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

        // When EnableRemoteNodeManagement is true and an item was recently deleted by the client,
        // skip re-adding it. The DeleteNodes call will eventually remove it from the server.
        if (_configuration.EnableRemoteNodeManagement && keysToAdd.Count > 0)
        {
            var filteredKeysToAdd = new List<string>();
            foreach (var key in keysToAdd)
            {
                if (remoteByKey.TryGetValue(key, out var remoteChild))
                {
                    var nodeId = ExpandedNodeId.ToNodeId(remoteChild.NodeId, session.NamespaceUris);
                    if (WasRecentlyDeleted(nodeId))
                    {
                        continue;
                    }
                }
                filteredKeysToAdd.Add(key);
            }
            keysToAdd = filteredKeysToAdd;
        }

        // Process additions
        foreach (var key in keysToAdd)
        {
            if (remoteByKey.TryGetValue(key, out var remoteChild))
            {
                var newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(
                    property, remoteChild, session, cancellationToken).ConfigureAwait(false);

                var nodeId = ExpandedNodeId.ToNodeId(remoteChild.NodeId, session.NamespaceUris);
                RegisterSubjectNodeId(newSubject, nodeId);

                // Add to dictionary FIRST - this attaches the subject to the parent which registers it
                // The subject must be registered before LoadSubjectAsync can set up monitored items
                if (!SubjectPropertyHelper.AddToDictionary(property, key, newSubject, _source))
                {
                    _logger.LogWarning(
                        "Cannot add to dictionary property '{PropertyName}': value is not a Dictionary<,>.",
                        property.Name);
                    continue;
                }
                localChildren = property.Children.ToDictionary(c => c.Index?.ToString() ?? "", c => c.Subject);

                // Now load and set up monitoring - subject is now registered
                var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                    newSubject, remoteChild, session, cancellationToken).ConfigureAwait(false);

                if (monitoredItems.Count > 0)
                {
                    var sessionManager = _source.SessionManager;
                    if (sessionManager is not null)
                    {
                        await sessionManager.AddMonitoredItemsAsync(
                            monitoredItems, session, cancellationToken).ConfigureAwait(false);
                    }
                }

                await _source.ReadAndApplySubjectValuesAsync(newSubject, session, cancellationToken).ConfigureAwait(false);
            }
        }

        // Process removals
        if (keysToRemove.Count > 0)
        {
            if (!SubjectPropertyHelper.RemoveFromDictionary(property, keysToRemove, _source))
            {
                _logger.LogWarning(
                    "Could not remove {RemovedCount} entries from dictionary property '{PropertyName}'.",
                    keysToRemove.Count, property.Name);
            }
        }
    }

    /// <summary>
    /// Processes node changes for a reference property by comparing remote state with local model.
    /// </summary>
    /// <param name="property">The reference property to sync.</param>
    /// <param name="parentNodeId">The NodeId of the parent subject node.</param>
    /// <param name="propertyName">The property name (browse name) to look for.</param>
    /// <param name="session">The OPC UA session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ProcessReferenceNodeChangesAsync(
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        string propertyName,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (!property.IsSubjectReference)
        {
            return;
        }

        // Browse to find the reference node under the parent
        var children = await OpcUaBrowseHelper.BrowseNodeAsync(session, parentNodeId, cancellationToken).ConfigureAwait(false);
        var referenceNode = children.FirstOrDefault(c => c.BrowseName.Name == propertyName);

        // Get current local value
        var localChildren = property.Children.ToList();
        var localSubject = localChildren.Count > 0 ? localChildren[0].Subject : null;
        var hasLocalValue = localSubject is not null;
        var hasRemoteValue = referenceNode is not null;

        if (hasRemoteValue && !hasLocalValue)
        {
            // Skip re-adding recently deleted items
            if (_configuration.EnableRemoteNodeManagement)
            {
                var referenceNodeId = ExpandedNodeId.ToNodeId(referenceNode!.NodeId, session.NamespaceUris);
                if (WasRecentlyDeleted(referenceNodeId))
                {
                    return;
                }
            }

            // Remote has value but local is null - create local subject
            var newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(
                property, referenceNode!, session, cancellationToken).ConfigureAwait(false);

            var nodeId = ExpandedNodeId.ToNodeId(referenceNode!.NodeId, session.NamespaceUris);
            RegisterSubjectNodeId(newSubject, nodeId);

            // Set property value FIRST - this attaches the subject to the parent which registers it
            // The subject must be registered before LoadSubjectAsync can set up monitored items
            SubjectPropertyHelper.SetReference(property, newSubject, _source);

            // Now load and set up monitoring - subject is now registered
            var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                newSubject, referenceNode!, session, cancellationToken).ConfigureAwait(false);

            if (monitoredItems.Count > 0)
            {
                var sessionManager = _source.SessionManager;
                if (sessionManager is not null)
                {
                    await sessionManager.AddMonitoredItemsAsync(
                        monitoredItems, session, cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                await _source.ReadAndApplySubjectValuesAsync(newSubject, session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read initial values for reference property '{PropertyName}'.", property.Name);
            }
        }
        else if (!hasRemoteValue && hasLocalValue)
        {
            // Remote has no value but local does - clear local
            if (_source.TryGetSubjectNodeId(localSubject!, out var oldNodeId) && oldNodeId is not null)
            {
                UnregisterSubjectNodeId(oldNodeId);
            }
            SubjectPropertyHelper.SetReference(property, null, _source);
        }
    }

    /// <summary>
    /// Processes a ModelChangeEvent from the server.
    /// </summary>
    public async Task ProcessModelChangeEventAsync(
        IReadOnlyList<ModelChangeStructureDataType> changes,
        ISession session,
        CancellationToken cancellationToken)
    {
        // TODO: Do we need to run under _structureSemaphore here? also check other places which operate on nodes/structure whether they are correctly synchronized
        
        foreach (var change in changes)
        {
            var verb = (ModelChangeStructureVerbMask)change.Verb;
            var affectedNodeId = change.Affected;

            if (verb.HasFlag(ModelChangeStructureVerbMask.NodeAdded))
            {
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
        var nodeDetails = await OpcUaBrowseHelper.ReadNodeDetailsAsync(session, nodeId, cancellationToken).ConfigureAwait(false);
        if (nodeDetails is null)
        {
            return;
        }

        var directParentNodeId = await OpcUaBrowseHelper.FindParentNodeIdAsync(session, nodeId, cancellationToken).ConfigureAwait(false);
        if (directParentNodeId is null)
        {
            return;
        }

        // Find the parent subject by traversing up the hierarchy
        IInterceptorSubject? parentSubject = null;
        var currentNodeId = directParentNodeId;
        const int maxDepth = 10;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            lock (_nodeIdToSubjectLock)
            {
                if (_nodeIdToSubject.TryGetValue(currentNodeId, out parentSubject))
                {
                    break;
                }
            }

            var nextParentNodeId = await OpcUaBrowseHelper.FindParentNodeIdAsync(session, currentNodeId, cancellationToken).ConfigureAwait(false);
            if (nextParentNodeId is null)
            {
                break;
            }
            currentNodeId = nextParentNodeId;
        }

        if (parentSubject is null)
        {
            return;
        }

        var registeredParent = parentSubject.TryGetRegisteredSubject();
        if (registeredParent is null)
        {
            return;
        }

        // Try to find which property this new node belongs to
        var browseName = nodeDetails.BrowseName.Name;
        foreach (var property in registeredParent.Properties)
        {
            var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
            if (propertyName is null)
            {
                continue;
            }

            // Check if this is a reference property
            if (property.IsSubjectReference)
            {
                // If the browse name matches the property name, this could be a new reference
                if (browseName == propertyName)
                {
                    _logger.LogDebug("ProcessNodeAddedAsync: Found matching reference property '{PropertyName}' for node '{BrowseName}'.",
                        propertyName, browseName);
                    // Get the parent's NodeId so we can browse for the reference
                    if (_source.TryGetSubjectNodeId(parentSubject, out var parentSubjectNodeId) && parentSubjectNodeId is not null)
                    {
                        await ProcessReferenceNodeChangesAsync(property, parentSubjectNodeId, propertyName, session, cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                continue;
            }

            if (!property.IsSubjectCollection && !property.IsSubjectDictionary)
            {
                continue;
            }

            // Check if this is a collection item (pattern: PropertyName[index])
            if (property.IsSubjectCollection && OpcUaBrowseHelper.TryParseCollectionIndex(browseName, propertyName, out _))
            {
                if (_source.TryGetSubjectNodeId(parentSubject, out var parentSubjectNodeId) && parentSubjectNodeId is not null)
                {
                    var containerNodeId = await OpcUaBrowseHelper.FindChildNodeIdAsync(session, parentSubjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
                    if (containerNodeId is not null)
                    {
                        await ProcessCollectionNodeChangesAsync(property, containerNodeId, session, cancellationToken).ConfigureAwait(false);
                    }
                }
                return;
            }

            // Check if this is a dictionary item (BrowseName = key)
            if (property.IsSubjectDictionary)
            {
                if (_source.TryGetSubjectNodeId(parentSubject, out var parentSubjectNodeId) && parentSubjectNodeId is not null)
                {
                    var containerNodeId = await OpcUaBrowseHelper.FindChildNodeIdAsync(session, parentSubjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
                    if (containerNodeId is not null && containerNodeId.Equals(directParentNodeId))
                    {
                        await ProcessDictionaryNodeChangesAsync(property, containerNodeId, session, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }
        }
    }

    private void ProcessNodeDeleted(NodeId nodeId)
    {
        IInterceptorSubject? deletedSubject = null;

        lock (_nodeIdToSubjectLock)
        {
            _nodeIdToSubject.Remove(nodeId, out deletedSubject);
        }

        if (deletedSubject is null)
        {
            _logger.LogDebug("ProcessNodeDeleted: NodeId {NodeId} not found in tracking.", nodeId);
            return;
        }

        _logger.LogInformation("ProcessNodeDeleted: Removing subject {Type} for NodeId {NodeId}.",
            deletedSubject.GetType().Name, nodeId);

        // Find the parent subject and property that contains this subject
        var registeredSubject = deletedSubject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            _logger.LogWarning("ProcessNodeDeleted: Subject {Type} is not registered.", deletedSubject.GetType().Name);
            return;
        }

        var parents = registeredSubject.Parents;
        if (parents.Length == 0)
        {
            _logger.LogWarning("ProcessNodeDeleted: Subject {Type} has no parent.", deletedSubject.GetType().Name);
            return;
        }

        var parentProperty = parents[0].Property;

        // Determine the type of parent property and remove accordingly
        if (parentProperty.IsSubjectReference)
        {
            // Clear the reference property
            SubjectPropertyHelper.SetReference(parentProperty, null, _source);
        }
        else if (parentProperty.IsSubjectCollection)
        {
            // Remove from collection - find the index by matching the subject
            var children = parentProperty.Children;
            for (var i = 0; i < children.Length; i++)
            {
                if (ReferenceEquals(children[i].Subject, deletedSubject) && children[i].Index is int index)
                {
                    SubjectPropertyHelper.RemoveFromCollectionByIndices(parentProperty, [index], _source); // TODO: Add warning when failed, same as other places
                    break;
                }
            }
        }
        else if (parentProperty.IsSubjectDictionary)
        {
            // Remove from dictionary
            if (parentProperty.GetValue() is IDictionary dictionary)
            {
                string? keyToRemove = null;
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    if (ReferenceEquals(entry.Value, deletedSubject))
                    {
                        keyToRemove = entry.Key as string;
                        break;
                    }
                }

                if (keyToRemove is not null)
                {
                    SubjectPropertyHelper.RemoveFromDictionary(parentProperty, [keyToRemove], _source); // TODO: Add warning when failed, same as other places
                }
            }
        }
    }

    private async Task ProcessReferenceAddedAsync(NodeId childNodeId, ISession session, CancellationToken cancellationToken)
    {
        // Only handle if we track this child subject
        if (!_nodeIdToSubject.TryGetValue(childNodeId, out var childSubject))
        {
            return;
        }

        // Browse inverse references to find all current parents
        var parentRefs = await OpcUaBrowseHelper.BrowseInverseReferencesAsync(
            session, childNodeId, cancellationToken).ConfigureAwait(false);

        foreach (var parentRef in parentRefs)
        {
            var parentNodeId = ExpandedNodeId.ToNodeId(parentRef.NodeId, session.NamespaceUris);

            // Check if we track this parent
            if (!_nodeIdToSubject.TryGetValue(parentNodeId, out var parentSubject))
            {
                continue;
            }

            var registeredParent = parentSubject.TryGetRegisteredSubject();
            if (registeredParent is null)
            {
                continue;
            }

            // Find reference property that should point to child
            foreach (var property in registeredParent.Properties)
            {
                // TODO: Could it also be referenced by collection or dictionary property (then we need to check children)?
                if (!property.IsSubjectReference)
                {
                    continue;
                }

                // If property is null locally, set it to the child
                var currentValue = property.GetValue();
                if (currentValue is null)
                {
                    var now = DateTimeOffset.UtcNow;
                    SubjectPropertyHelper.SetReference(property, childSubject, _source, now, now);
                    _logger.LogDebug(
                        "ReferenceAdded: Set {ParentType}.{Property} = {ChildType}",
                        parentSubject.GetType().Name,
                        property.Name,
                        childSubject.GetType().Name);
                }
            }
        }
    }

    private void ProcessReferenceDeleted(NodeId childNodeId)
    {
        // Only handle if we track this child subject
        if (!_nodeIdToSubject.TryGetValue(childNodeId, out var childSubject))
        {
            return;
        }

        // Find all tracked parents that currently reference this child
        lock (_nodeIdToSubjectLock)
        {
            foreach (var (_, parentSubject) in _nodeIdToSubject)
            {
                var registeredParent = parentSubject.TryGetRegisteredSubject();
                if (registeredParent is null)
                {
                    continue;
                }

                foreach (var property in registeredParent.Properties)
                {
                    if (!property.IsSubjectReference)
                    {
                        continue;
                    }

                    // If this property references the child, clear it
                    var currentValue = property.GetValue();
                    if (ReferenceEquals(currentValue, childSubject))
                    {
                        var now = DateTimeOffset.UtcNow;
                        SubjectPropertyHelper.SetReference(property, null, _source, now, now);
                        _logger.LogDebug(
                            "ReferenceDeleted: Cleared {ParentType}.{Property}",
                            parentSubject.GetType().Name,
                            property.Name);
                    }
                }
            }
        }
    }
}

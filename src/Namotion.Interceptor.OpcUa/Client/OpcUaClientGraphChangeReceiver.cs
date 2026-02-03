using System.Collections;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Receives and processes OPC UA node changes (from ModelChangeEvents or periodic resync) to update the C# model.
/// Compares remote address space with local model and creates/removes subjects as needed.
/// </summary>
internal class OpcUaClientGraphChangeReceiver
{
    private readonly OpcUaSubjectClientSource _source;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly ILogger _logger;
    private readonly GraphChangeApplier _graphChangeApplier;

    // Shared registry between subjects and NodeIds - owned by OpcUaSubjectClientSource
    private readonly OpcUaClientSubjectRegistry _subjectRegistry;

    // Track whether we're currently processing a remote ModelChangeEvent.
    // Used to prevent adding monitored items during remote change processing.
    private volatile bool _isProcessingRemoteChange;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpcUaClientGraphChangeReceiver"/> class.
    /// </summary>
    /// <param name="source">The OPC UA client source.</param>
    /// <param name="subjectRegistry">The shared subject registry.</param>
    /// <param name="configuration">The client configuration.</param>
    /// <param name="subjectLoader">The subject loader for creating new subjects.</param>
    /// <param name="logger">The logger.</param>
    public OpcUaClientGraphChangeReceiver(
        OpcUaSubjectClientSource source,
        OpcUaClientSubjectRegistry subjectRegistry,
        OpcUaClientConfiguration configuration,
        OpcUaSubjectLoader subjectLoader,
        ILogger logger)
    {
        _source = source;
        _subjectRegistry = subjectRegistry;
        _configuration = configuration;
        _subjectLoader = subjectLoader;
        _logger = logger;
        _graphChangeApplier = new GraphChangeApplier();
    }

    /// <summary>
    /// Loads a subject's children, reads initial values, and adds monitored items if not processing remote changes.
    /// </summary>
    private async Task LoadAndMonitorSubjectAsync(
        IInterceptorSubject subject,
        ReferenceDescription nodeDetails,
        ISession session,
        CancellationToken cancellationToken)
    {
        var monitoredItems = await _subjectLoader.LoadSubjectAsync(
            subject, nodeDetails, session, cancellationToken).ConfigureAwait(false);

        try
        {
            await _source.ReadAndApplySubjectValuesAsync(subject, session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read initial values for subject {Type}.", subject.GetType().Name);
        }

        if (!_isProcessingRemoteChange && monitoredItems.Count > 0)
        {
            var sessionManager = _source.SessionManager;
            if (sessionManager is not null)
            {
                await sessionManager.AddMonitoredItemsAsync(
                    monitoredItems, session, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Clears local state. Note: The subject registry is shared and owned by OpcUaSubjectClientSource.
    /// </summary>
    public void Clear()
    {
        // Nothing to clear - registry is owned by OpcUaSubjectClientSource
    }

    /// <summary>
    /// Checks if a NodeId was recently deleted by the client.
    /// Used by periodic resync to avoid re-adding items that the client intentionally removed.
    /// </summary>
    /// <param name="nodeId">The NodeId to check.</param>
    /// <returns>True if the NodeId was recently deleted, false otherwise.</returns>
    public bool WasRecentlyDeleted(NodeId nodeId)
    {
        return _subjectRegistry.WasRecentlyDeleted(nodeId);
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
                    // Check collection structure mode
                    var nodeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(property);
                    var collectionStructure = nodeConfiguration?.CollectionStructure ?? CollectionNodeStructure.Container;
                    if (collectionStructure == CollectionNodeStructure.Flat)
                    {
                        // Flat mode: items are directly under the parent node
                        await ProcessCollectionNodeChangesAsync(property, subjectNodeId, session, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        // Container mode: items are under a container folder
                        var containerNodeId = await OpcUaHelper.FindChildNodeIdAsync(session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
                        if (containerNodeId is not null)
                        {
                            await ProcessCollectionNodeChangesAsync(property, containerNodeId, session, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                else if (property.IsSubjectDictionary)
                {
                    var containerNodeId = await OpcUaHelper.FindChildNodeIdAsync(session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
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
        var remoteChildren = await OpcUaHelper.BrowseNodeAsync(session, containerNodeId, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Browse '{Container}' returned {Count} children: {Names}",
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
            if (OpcUaHelper.TryParseCollectionIndex(browseName, propertyName, out var index))
            {
                remoteByIndex[index] = remoteChild;
            }
        }

        // Find differences using actual NodeId indices (not registry list positions)
        var remoteIndices = new HashSet<int>(remoteByIndex.Keys);
        var localByIndex = new Dictionary<int, IInterceptorSubject>();
        foreach (var child in localChildren)
        {
            if (child.Subject is null)
            {
                continue;
            }

            // Get actual index from the subject's NodeId, not from child.Index (which is list position)
            if (_subjectRegistry.TryGetExternalId(child.Subject, out var childNodeId) && childNodeId is not null)
            {
                var childNodeIdStr = childNodeId.Identifier as string;
                if (childNodeIdStr is not null && TryParseCollectionIndexFromNodeId(childNodeIdStr, out var nodeIdIndex))
                {
                    localByIndex[nodeIdIndex] = child.Subject;
                }
            }
        }
        var localIndices = new HashSet<int>(localByIndex.Keys);

        var indicesToAdd = remoteIndices.Except(localIndices).OrderBy(i => i).ToList();
        var indicesToRemove = localIndices.Except(remoteIndices).OrderByDescending(i => i).ToList();

        _logger.LogDebug("Collection sync for '{Property}': remote={Remote}, local={Local}, toAdd={Add}, toRemove={Remove}",
            property.Name, string.Join(",", remoteIndices), string.Join(",", localIndices),
            string.Join(",", indicesToAdd), string.Join(",", indicesToRemove));

        // When EnableGraphChangePublishing is true and an item was recently deleted by the client,
        // skip re-adding it. The DeleteNodes call will eventually remove it from the server.
        if (_configuration.EnableGraphChangePublishing && indicesToAdd.Count > 0)
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
                // Capture remoteChild for the factory closure
                var capturedRemoteChild = remoteChild;

                // Add to collection using factory pattern - subject only created if validation passes
                // LoadSubjectAsync will handle registration via TrackSubject
                var newSubject = await _graphChangeApplier.AddToCollectionAsync(
                    property,
                    () => _configuration.SubjectFactory.CreateSubjectForPropertyAsync(
                        property, capturedRemoteChild, session, cancellationToken),
                    _source).ConfigureAwait(false);

                if (newSubject is null)
                {
                    _logger.LogWarning(
                        "Cannot add to collection property '{PropertyName}': value is not a collection.",
                        property.Name);
                    continue;
                }

                // Load, read initial values, and set up monitoring
                await LoadAndMonitorSubjectAsync(newSubject, remoteChild, session, cancellationToken).ConfigureAwait(false);
            }
        }

        // Process removals by subject reference (indices are NodeId indices, not model positions)
        foreach (var index in indicesToRemove)
        {
            if (localByIndex.TryGetValue(index, out var subjectToRemove))
            {
                if (!_graphChangeApplier.RemoveFromCollection(property, subjectToRemove, _source))
                {
                    _logger.LogWarning(
                        "Could not remove subject at NodeId index {Index} from collection property '{PropertyName}'.",
                        index, property.Name);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Could not find subject for NodeId index {Index} in collection property '{PropertyName}'.",
                    index, property.Name);
            }
        }
    }

    /// <summary>
    /// Processes a single collection item being added via NodeAdded event.
    /// This is more reliable than re-browsing the container, which may not return new nodes immediately.
    /// </summary>
    private async Task ProcessCollectionItemAddedAsync(
        RegisteredSubjectProperty property,
        NodeId nodeId,
        ReferenceDescription nodeDetails,
        int index,
        ISession session,
        CancellationToken cancellationToken)
    {
        if (!property.IsSubjectCollection)
        {
            return;
        }

        // Check if this NodeId index already exists in the local collection
        // Use actual NodeId indices, not registry list positions (child.Index)
        var localChildren = property.Children.ToList();
        var localIndices = new HashSet<int>();
        foreach (var child in localChildren)
        {
            if (child.Subject is not null &&
                _subjectRegistry.TryGetExternalId(child.Subject, out var childNodeId) &&
                childNodeId is not null)
            {
                var childNodeIdStr = childNodeId.Identifier as string;
                if (childNodeIdStr is not null && TryParseCollectionIndexFromNodeId(childNodeIdStr, out var nodeIdIndex))
                {
                    localIndices.Add(nodeIdIndex);
                }
            }
        }
        if (localIndices.Contains(index))
        {
            return;
        }

        // Skip re-adding recently deleted items
        if (_configuration.EnableGraphChangePublishing && WasRecentlyDeleted(nodeId))
        {
            return;
        }

        // Add to collection using factory pattern - subject only created if validation passes
        // LoadSubjectAsync will handle registration via TrackSubject
        var newSubject = await _graphChangeApplier.AddToCollectionAsync(
            property,
            () => _configuration.SubjectFactory.CreateSubjectForPropertyAsync(
                property, nodeDetails, session, cancellationToken),
            _source).ConfigureAwait(false);

        if (newSubject is null)
        {
            _logger.LogWarning(
                "ProcessCollectionItemAddedAsync: Cannot add to collection property '{PropertyName}': value is not a collection.",
                property.Name);
            return;
        }

        // Load, read initial values, and set up monitoring
        await LoadAndMonitorSubjectAsync(newSubject, nodeDetails, session, cancellationToken).ConfigureAwait(false);
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
        var remoteChildren = await OpcUaHelper.BrowseNodeAsync(session, containerNodeId, cancellationToken).ConfigureAwait(false);

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

        // When EnableGraphChangePublishing is true and an item was recently deleted by the client,
        // skip re-adding it. The DeleteNodes call will eventually remove it from the server.
        if (_configuration.EnableGraphChangePublishing && keysToAdd.Count > 0)
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
                // Capture variables for the factory closure
                var capturedKey = key;
                var capturedRemoteChild = remoteChild;

                // Add to dictionary using factory pattern - subject only created if validation passes
                // LoadSubjectAsync will handle registration via TrackSubject
                var newSubject = await _graphChangeApplier.AddToDictionaryAsync(
                    property,
                    capturedKey,
                    () => _configuration.SubjectFactory.CreateSubjectForPropertyAsync(
                        property, capturedRemoteChild, session, cancellationToken),
                    _source).ConfigureAwait(false);

                if (newSubject is null)
                {
                    _logger.LogWarning(
                        "Cannot add to dictionary property '{PropertyName}': value is not a dictionary.",
                        property.Name);
                    continue;
                }
                localChildren = property.Children.ToDictionary(c => c.Index?.ToString() ?? "", c => c.Subject);

                // Load, read initial values, and set up monitoring
                await LoadAndMonitorSubjectAsync(newSubject, remoteChild, session, cancellationToken).ConfigureAwait(false);
            }
        }

        // Process removals
        foreach (var key in keysToRemove)
        {
            if (!_graphChangeApplier.RemoveFromDictionary(property, key, _source))
            {
                _logger.LogWarning(
                    "Could not remove entry with key '{Key}' from dictionary property '{PropertyName}': value is not a dictionary.",
                    key, property.Name);
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
        var children = await OpcUaHelper.BrowseNodeAsync(session, parentNodeId, cancellationToken).ConfigureAwait(false);
        var referenceNode = children.FirstOrDefault(c => c.BrowseName.Name == propertyName);

        // Get current local value
        var localChildren = property.Children.ToList();
        var localSubject = localChildren.Count > 0 ? localChildren[0].Subject : null;
        var hasLocalValue = localSubject is not null;
        var hasRemoteValue = referenceNode is not null;

        // Check if the remote and local refer to the same node (for replacement detection)
        var remoteNodeId = hasRemoteValue
            ? ExpandedNodeId.ToNodeId(referenceNode!.NodeId, session.NamespaceUris)
            : null;
        NodeId? localNodeId = null;
        if (hasLocalValue && _source.TryGetSubjectNodeId(localSubject!, out var existingNodeId))
        {
            localNodeId = existingNodeId;
        }

        var needsReplacement = hasRemoteValue && hasLocalValue &&
            remoteNodeId is not null && localNodeId is not null &&
            !remoteNodeId.Equals(localNodeId);

        if (needsReplacement)
        {
            // Remote has a DIFFERENT value than local - this is a replacement
            // First, unregister the old subject
            if (localNodeId is not null)
            {
                _subjectRegistry.UnregisterByExternalId(localNodeId, out _, out _, out _);
            }

            // Skip re-adding recently deleted items
            if (_configuration.EnableGraphChangePublishing && WasRecentlyDeleted(remoteNodeId!))
            {
                _graphChangeApplier.RemoveReference(property, _source);
                return;
            }

            // Set reference using factory pattern - subject only created if validation passes
            // LoadSubjectAsync will handle registration via TrackSubject
            var newSubject = await _graphChangeApplier.SetReferenceAsync(
                property,
                () => _configuration.SubjectFactory.CreateSubjectForPropertyAsync(
                    property, referenceNode!, session, cancellationToken),
                _source).ConfigureAwait(false);

            if (newSubject is null)
            {
                _logger.LogWarning(
                    "Cannot set reference property '{PropertyName}': value is not a reference.",
                    property.Name);
                return;
            }

            // Load, read initial values, and set up monitoring
            await LoadAndMonitorSubjectAsync(newSubject, referenceNode!, session, cancellationToken).ConfigureAwait(false);
        }
        else if (hasRemoteValue && !hasLocalValue)
        {
            // Skip re-adding recently deleted items
            if (_configuration.EnableGraphChangePublishing)
            {
                var referenceNodeId = ExpandedNodeId.ToNodeId(referenceNode!.NodeId, session.NamespaceUris);
                if (WasRecentlyDeleted(referenceNodeId))
                {
                    return;
                }
            }

            // Set reference using factory pattern - subject only created if validation passes
            // LoadSubjectAsync will handle registration via TrackSubject
            var newSubject = await _graphChangeApplier.SetReferenceAsync(
                property,
                () => _configuration.SubjectFactory.CreateSubjectForPropertyAsync(
                    property, referenceNode!, session, cancellationToken),
                _source).ConfigureAwait(false);

            if (newSubject is null)
            {
                _logger.LogWarning(
                    "Cannot set reference property '{PropertyName}': value is not a reference.",
                    property.Name);
                return;
            }

            // Load, read initial values, and set up monitoring
            await LoadAndMonitorSubjectAsync(newSubject, referenceNode!, session, cancellationToken).ConfigureAwait(false);
        }
        else if (!hasRemoteValue && hasLocalValue)
        {
            // Remote has no value but local does - clear local
            if (_source.TryGetSubjectNodeId(localSubject!, out var oldNodeId) && oldNodeId is not null)
            {
                _subjectRegistry.UnregisterByExternalId(oldNodeId, out _, out _, out _);
            }
            _graphChangeApplier.RemoveReference(property, _source);
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
        // Set flag to prevent DeleteNodes calls back to server for deletions that originated from the server
        _isProcessingRemoteChange = true;
        try
        {
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
                    await ProcessReferenceDeletedAsync(affectedNodeId, session, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _isProcessingRemoteChange = false;
        }
    }

    private async Task ProcessNodeAddedAsync(NodeId nodeId, ISession session, CancellationToken cancellationToken)
    {
        // Skip nodes that are already tracked - this handles the initial burst of events
        // when connecting to a server with existing nodes
        if (_subjectRegistry.TryGetSubject(nodeId, out _))
        {
            _logger.LogDebug("ProcessNodeAdded: Skipping already-tracked NodeId {NodeId}.", nodeId);
            return;
        }

        _logger.LogDebug("ProcessNodeAdded: Processing NodeId {NodeId}.", nodeId);

        var nodeDetails = await OpcUaHelper.ReadNodeDetailsAsync(session, nodeId, cancellationToken).ConfigureAwait(false);
        if (nodeDetails is null)
        {
            _logger.LogDebug("ProcessNodeAdded: Could not read node details for {NodeId}.", nodeId);
            return;
        }

        var directParentNodeId = await OpcUaHelper.FindParentNodeIdAsync(session, nodeId, cancellationToken).ConfigureAwait(false);
        if (directParentNodeId is null)
        {
            _logger.LogDebug("ProcessNodeAdded: Could not find parent for {NodeId}.", nodeId);
            return;
        }

        // Find the parent subject by traversing up the hierarchy
        IInterceptorSubject? parentSubject = null;
        var currentNodeId = directParentNodeId;
        const int maxDepth = 10;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            if (_subjectRegistry.TryGetSubject(currentNodeId, out parentSubject))
            {
                break;
            }

            var nextParentNodeId = await OpcUaHelper.FindParentNodeIdAsync(session, currentNodeId, cancellationToken).ConfigureAwait(false);
            if (nextParentNodeId is null)
            {
                break;
            }
            currentNodeId = nextParentNodeId;
        }

        if (parentSubject is null)
        {
            _logger.LogDebug("ProcessNodeAdded: Could not find parent subject for {NodeId} (searched up to {ParentNodeId}).", nodeId, currentNodeId);
            return;
        }

        var registeredParent = parentSubject.TryGetRegisteredSubject();
        if (registeredParent is null)
        {
            _logger.LogDebug("ProcessNodeAdded: Parent subject not registered for {NodeId}.", nodeId);
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
                    // Get the parent's NodeId so we can browse for the reference
                    if (_source.TryGetSubjectNodeId(parentSubject, out var parentSubjectNodeId) && parentSubjectNodeId is not null)
                    {
                        await ProcessReferenceNodeChangesAsync(property, parentSubjectNodeId, propertyName, session, cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                continue;
            }

            if (property is { IsSubjectCollection: false, IsSubjectDictionary: false })
            {
                continue;
            }

            // Check if this is a collection item (pattern: PropertyName[index])
            if (property.IsSubjectCollection && OpcUaHelper.TryParseCollectionIndex(browseName, propertyName, out var parsedIndex))
            {
                // Directly add this item to the collection from the NodeAdded event
                // This is more reliable than re-browsing, which may not return the new node immediately
                await ProcessCollectionItemAddedAsync(property, nodeId, nodeDetails, parsedIndex, session, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Check if this is a dictionary item (BrowseName = key)
            if (property.IsSubjectDictionary)
            {
                if (_source.TryGetSubjectNodeId(parentSubject, out var parentSubjectNodeId) && parentSubjectNodeId is not null)
                {
                    var containerNodeId = await OpcUaHelper.FindChildNodeIdAsync(session, parentSubjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
                    if (containerNodeId is not null && containerNodeId.Equals(directParentNodeId))
                    {
                        _logger.LogDebug("ProcessNodeAdded: Processing dictionary item {BrowseName} for property {PropertyName}.", browseName, propertyName);
                        await ProcessDictionaryNodeChangesAsync(property, containerNodeId, session, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        _logger.LogDebug("ProcessNodeAdded: Dictionary container mismatch for {NodeId}. Expected {Expected}, got {Actual}.",
                            nodeId, directParentNodeId, containerNodeId);
                    }
                }
                else
                {
                    _logger.LogDebug("ProcessNodeAdded: Could not get parent NodeId for dictionary property {PropertyName}.", propertyName);
                }
            }
        }

        _logger.LogDebug("ProcessNodeAdded: No matching property found for {NodeId} with BrowseName {BrowseName}.", nodeId, browseName);
    }

    private void ProcessNodeDeleted(NodeId nodeId)
    {
        if (!_subjectRegistry.UnregisterByExternalId(nodeId, out var deletedSubject, out _, out _) || deletedSubject is null)
        {
            _logger.LogDebug("ProcessNodeDeleted: NodeId {NodeId} not found in tracking.", nodeId);
            return;
        }

        _logger.LogDebug("ProcessNodeDeleted: Removing subject {Type} for NodeId {NodeId}.",
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

        // Remove from ALL parents (node is deleted from address space)
        // Multi-parent subjects (shared subjects) need to be cleaned up from all parent properties
        foreach (var parent in parents)
        {
            RemoveSubjectFromParent(deletedSubject, parent.Property, nodeId);
        }
    }

    /// <summary>
    /// Removes a subject from a parent property (reference, collection, or dictionary).
    /// This helper handles all parent property types and is used by ProcessNodeDeleted
    /// to clean up subjects from all their parents.
    /// </summary>
    /// <param name="subject">The subject to remove from the parent.</param>
    /// <param name="parentProperty">The parent property to remove from.</param>
    /// <param name="deletedNodeId">The NodeId of the deleted subject (needed because subject was already unregistered).</param>
    private void RemoveSubjectFromParent(IInterceptorSubject subject, RegisteredSubjectProperty parentProperty, NodeId deletedNodeId)
    {
        if (parentProperty.IsSubjectReference)
        {
            // Clear the reference property
            _graphChangeApplier.RemoveReference(parentProperty, _source);
        }
        else if (parentProperty.IsSubjectCollection)
        {
            // Capture existing subjects with their NodeId indices before removal
            // We need this to update NodeId registrations after reindexing
            // IMPORTANT: We must use the actual NodeId index (from OPC UA), not the registry's
            // list position (child.Index), because the registry uses list positions while
            // NodeIds use the actual collection indices from the server.
            var subjectsToReindex = new List<(IInterceptorSubject Subject, int OldIndex)>();
            var children = parentProperty.Children;
            int? removedIndex = null;

            // Get the removed index from the deleted NodeId (the subject was already unregistered)
            var deletedNodeIdStr = deletedNodeId.Identifier as string;
            if (deletedNodeIdStr is not null && TryParseCollectionIndexFromNodeId(deletedNodeIdStr, out var deletedIndex))
            {
                removedIndex = deletedIndex;
            }

            // Collect OTHER subjects (not the deleted one) that may need NodeId reindexing
            foreach (var child in children)
            {
                // Skip the deleted subject (we already got its index from deletedNodeId)
                // Also skip null subjects
                if (child.Subject is null || ReferenceEquals(child.Subject, subject))
                {
                    continue;
                }

                // Get the actual index from the NodeId, not from child.Index (which is list position)
                if (!_subjectRegistry.TryGetExternalId(child.Subject, out var childNodeId) || childNodeId is null)
                {
                    continue;
                }

                var childNodeIdStr = childNodeId.Identifier as string;
                if (childNodeIdStr is null || !TryParseCollectionIndexFromNodeId(childNodeIdStr, out var nodeIdIndex))
                {
                    continue;
                }

                subjectsToReindex.Add((child.Subject, nodeIdIndex));
            }

            if (removedIndex is { } removedIdx)
            {
                // Use RemoveFromCollection by subject reference (not by index)
                // because removedIdx is the NodeId index, not the list position
                if (_graphChangeApplier.RemoveFromCollection(parentProperty, subject, _source))
                {
                    // Update NodeId registrations for items that got reindexed
                    UpdateCollectionNodeIdRegistrationsAfterRemoval(subjectsToReindex, removedIdx);
                }
                else
                {
                    _logger.LogWarning(
                        "RemoveSubjectFromParent: Could not remove subject from collection property '{PropertyName}'.",
                        parentProperty.Name);
                }
            }
        }
        else if (parentProperty.IsSubjectDictionary)
        {
            // Remove from dictionary - find the key first
            if (parentProperty.GetValue() is IDictionary dictionary)
            {
                object? keyToRemove = null;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (ReferenceEquals(entry.Value, subject))
                    {
                        keyToRemove = entry.Key;
                        break;
                    }
                }

                if (keyToRemove is not null)
                {
                    _graphChangeApplier.RemoveFromDictionary(parentProperty, keyToRemove, _source);
                }
            }
        }
    }

    /// <summary>
    /// Updates NodeId registrations for collection items after one item is removed.
    /// When an item is removed from a collection, remaining items get reindexed by the server
    /// (e.g., [1] becomes [0]). This method updates the client's NodeId tracking to match.
    /// </summary>
    private void UpdateCollectionNodeIdRegistrationsAfterRemoval(
        List<(IInterceptorSubject Subject, int OldIndex)> subjectsWithOldIndices,
        int removedIndex)
    {
        foreach (var (subject, oldIndex) in subjectsWithOldIndices)
        {
            // Only items after the removed index need reindexing
            if (oldIndex <= removedIndex)
            {
                continue;
            }

            var newIndex = oldIndex - 1;

            if (_subjectRegistry.TryGetExternalId(subject, out var existingNodeId) && existingNodeId is not null)
            {
                // Construct new NodeId by replacing the index in the string representation
                var existingNodeIdStr = existingNodeId.ToString();
                var indexPattern = $"[{oldIndex}]";
                var newIndexPattern = $"[{newIndex}]";
                if (existingNodeIdStr.Contains(indexPattern))
                {
                    var newNodeIdStr = existingNodeIdStr.Replace(indexPattern, newIndexPattern);
                    var newNodeId = new NodeId(newNodeIdStr);

                    // Update the mapping with the new NodeId (handles both directions atomically)
                    _subjectRegistry.UpdateExternalId(subject, newNodeId);
                }
            }
        }
    }

    /// <summary>
    /// Parses the collection index from a NodeId string like "Root.Collection[3]" or "Root.Collection[3].Property".
    /// Returns the last bracketed index found in the path.
    /// </summary>
    private static bool TryParseCollectionIndexFromNodeId(string nodeIdStr, out int index)
    {
        index = -1;

        // Find the last occurrence of [N] pattern
        var lastBracketEnd = nodeIdStr.LastIndexOf(']');
        if (lastBracketEnd == -1)
        {
            return false;
        }

        var lastBracketStart = nodeIdStr.LastIndexOf('[', lastBracketEnd);
        if (lastBracketStart == -1 || lastBracketStart >= lastBracketEnd - 1)
        {
            return false;
        }

        var indexStr = nodeIdStr.Substring(lastBracketStart + 1, lastBracketEnd - lastBracketStart - 1);
        return int.TryParse(indexStr, out index) && index >= 0;
    }

    private async Task ProcessReferenceAddedAsync(NodeId childNodeId, ISession session, CancellationToken cancellationToken)
    {
        _logger.LogDebug("ProcessReferenceAddedAsync: Processing ReferenceAdded for NodeId {NodeId}", childNodeId);

        // Only handle if we track this child subject
        if (!_subjectRegistry.TryGetSubject(childNodeId, out var childSubject) || childSubject is null)
        {
            _logger.LogDebug("ProcessReferenceAddedAsync: NodeId {NodeId} not found in tracked subjects", childNodeId);
            return;
        }

        _logger.LogDebug("ProcessReferenceAddedAsync: Found tracked subject {Type} for NodeId {NodeId}",
            childSubject.GetType().Name, childNodeId);

        // Since ReferenceAdded doesn't tell us WHICH parent added the reference,
        // we need to check all tracked subjects' collection/dictionary properties
        // to see if any should now contain this child.

        await ProcessTrackedSubjectPropertiesAsync(
            childNodeId,
            childSubject,
            session,
            cancellationToken,
            isReferenceAdded: true).ConfigureAwait(false);
    }

    private async Task ProcessReferenceDeletedAsync(NodeId childNodeId, ISession session, CancellationToken cancellationToken)
    {
        _logger.LogDebug("ProcessReferenceDeleted: Processing ReferenceDeleted for NodeId {NodeId}", childNodeId);

        // Only handle if we track this child subject
        if (!_subjectRegistry.TryGetSubject(childNodeId, out var childSubject) || childSubject is null)
        {
            _logger.LogDebug("ProcessReferenceDeleted: NodeId {NodeId} not found in tracked subjects", childNodeId);
            return;
        }

        _logger.LogDebug("ProcessReferenceDeleted: Found tracked subject {Type} for NodeId {NodeId}",
            childSubject.GetType().Name, childNodeId);

        // Find all tracked parents that currently reference this child
        // We need to check the SERVER to see which containers still have the reference
        await ProcessTrackedSubjectPropertiesAsync(
            childNodeId,
            childSubject,
            session,
            cancellationToken,
            isReferenceAdded: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Iterates over all tracked subjects and their properties, processing reference changes.
    /// This is the common iteration logic extracted from ProcessReferenceAddedAsync and ProcessReferenceDeletedAsync.
    /// </summary>
    private async Task ProcessTrackedSubjectPropertiesAsync(
        NodeId childNodeId,
        IInterceptorSubject childSubject,
        ISession session,
        CancellationToken cancellationToken,
        bool isReferenceAdded)
    {
        foreach (var trackedSubject in _source.GetTrackedSubjects())
        {
            var registeredSubject = trackedSubject.TryGetRegisteredSubject();
            if (registeredSubject is null)
            {
                continue;
            }

            // Get this subject's NodeId to browse its containers
            if (!_source.TryGetSubjectNodeId(trackedSubject, out var subjectNodeId) || subjectNodeId is null)
            {
                continue;
            }

            foreach (var property in registeredSubject.Properties)
            {
                if (property.IsSubjectReference)
                {
                    await ProcessReferencePropertyChangeAsync(
                        property, trackedSubject, subjectNodeId, childNodeId, childSubject,
                        session, cancellationToken, isReferenceAdded).ConfigureAwait(false);
                }
                else if (property.IsSubjectCollection)
                {
                    await ProcessCollectionPropertyChangeAsync(
                        property, trackedSubject, subjectNodeId, childNodeId, childSubject,
                        session, cancellationToken, isReferenceAdded).ConfigureAwait(false);
                }
                else if (property.IsSubjectDictionary)
                {
                    await ProcessDictionaryPropertyChangeAsync(
                        property, trackedSubject, subjectNodeId, childNodeId, childSubject,
                        session, cancellationToken, isReferenceAdded).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Processes reference property changes (add or delete) for a single reference property.
    /// </summary>
    private async Task ProcessReferencePropertyChangeAsync(
        RegisteredSubjectProperty property,
        IInterceptorSubject trackedSubject,
        NodeId subjectNodeId,
        NodeId childNodeId,
        IInterceptorSubject childSubject,
        ISession session,
        CancellationToken cancellationToken,
        bool isReferenceAdded)
    {
        var currentValue = property.GetValue();
        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
        if (propertyName is null)
        {
            return;
        }

        if (isReferenceAdded)
        {
            // For add: only process if currently null
            if (currentValue is not null)
            {
                return;
            }

            var childRefNodeId = await OpcUaHelper.FindChildNodeIdAsync(
                session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);

            if (childRefNodeId is not null && childRefNodeId.Equals(childNodeId))
            {
                // We already have the subject, use factory that returns it directly
                await _graphChangeApplier.SetReferenceAsync(
                    property,
                    () => Task.FromResult(childSubject),
                    _source).ConfigureAwait(false);
                _logger.LogDebug(
                    "ReferenceAdded: Set {ParentType}.{Property} = {ChildType}",
                    trackedSubject.GetType().Name,
                    property.Name,
                    childSubject.GetType().Name);
            }
        }
        else
        {
            // For delete: only process if currently references the child
            if (!ReferenceEquals(currentValue, childSubject))
            {
                return;
            }

            var refNodeId = await OpcUaHelper.FindChildNodeIdAsync(
                session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);

            if (refNodeId is null || !refNodeId.Equals(childNodeId))
            {
                // Reference no longer exists on server - remove it
                _graphChangeApplier.RemoveReference(property, _source);
                _logger.LogDebug(
                    "ReferenceDeleted: Cleared {ParentType}.{Property}",
                    trackedSubject.GetType().Name,
                    property.Name);
            }
        }
    }

    /// <summary>
    /// Gets the container NodeId for a collection property, handling both flat and container modes.
    /// </summary>
    private async Task<NodeId?> GetCollectionContainerNodeIdAsync(
        RegisteredSubjectProperty property,
        NodeId subjectNodeId,
        string propertyName,
        ISession session,
        CancellationToken cancellationToken)
    {
        var nodeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(property);
        var collectionStructure = nodeConfiguration?.CollectionStructure ?? CollectionNodeStructure.Container;

        if (collectionStructure == CollectionNodeStructure.Flat)
        {
            return subjectNodeId;
        }

        return await OpcUaHelper.FindChildNodeIdAsync(
            session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks if a container contains a child node by browsing the container.
    /// Returns the matching ReferenceDescription if found, null otherwise.
    /// </summary>
    private async Task<ReferenceDescription?> FindChildInContainerAsync(
        NodeId containerNodeId,
        NodeId childNodeId,
        ISession session,
        CancellationToken cancellationToken)
    {
        var containerChildren = await OpcUaHelper.BrowseNodeAsync(
            session, containerNodeId, cancellationToken).ConfigureAwait(false);

        foreach (var child in containerChildren)
        {
            var refNodeId = ExpandedNodeId.ToNodeId(child.NodeId, session.NamespaceUris);
            if (refNodeId.Equals(childNodeId))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Processes collection property changes (add or delete) for a single collection property.
    /// </summary>
    private async Task ProcessCollectionPropertyChangeAsync(
        RegisteredSubjectProperty property,
        IInterceptorSubject trackedSubject,
        NodeId subjectNodeId,
        NodeId childNodeId,
        IInterceptorSubject childSubject,
        ISession session,
        CancellationToken cancellationToken,
        bool isReferenceAdded)
    {
        var children = property.Children;
        var containsChild = children.Any(c => ReferenceEquals(c.Subject, childSubject));

        // For add: skip if already contains; for delete: skip if doesn't contain
        if (isReferenceAdded && containsChild)
        {
            return;
        }
        if (!isReferenceAdded && !containsChild)
        {
            return;
        }

        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
        if (propertyName is null)
        {
            return;
        }

        var containerNodeId = await GetCollectionContainerNodeIdAsync(
            property, subjectNodeId, propertyName, session, cancellationToken).ConfigureAwait(false);

        if (containerNodeId is null)
        {
            return;
        }

        var childInContainer = await FindChildInContainerAsync(
            containerNodeId, childNodeId, session, cancellationToken).ConfigureAwait(false);

        if (isReferenceAdded)
        {
            if (childInContainer is not null)
            {
                // Add to collection - we already have the subject, use factory that returns it directly
                var addedSubject = await _graphChangeApplier.AddToCollectionAsync(
                    property,
                    () => Task.FromResult(childSubject),
                    _source).ConfigureAwait(false);

                if (addedSubject is not null)
                {
                    _logger.LogDebug(
                        "ReferenceAdded: Added {ChildType} to collection {ParentType}.{Property}",
                        childSubject.GetType().Name,
                        trackedSubject.GetType().Name,
                        property.Name);
                }
            }
        }
        else
        {
            if (childInContainer is null)
            {
                // Child no longer in this container - remove it from the local collection
                if (_graphChangeApplier.RemoveFromCollection(property, childSubject, _source))
                {
                    _logger.LogDebug(
                        "ReferenceDeleted: Removed {ChildType} from collection {ParentType}.{Property}",
                        childSubject.GetType().Name,
                        trackedSubject.GetType().Name,
                        property.Name);
                }
            }
        }
    }

    /// <summary>
    /// Processes dictionary property changes (add or delete) for a single dictionary property.
    /// </summary>
    private async Task ProcessDictionaryPropertyChangeAsync(
        RegisteredSubjectProperty property,
        IInterceptorSubject trackedSubject,
        NodeId subjectNodeId,
        NodeId childNodeId,
        IInterceptorSubject childSubject,
        ISession session,
        CancellationToken cancellationToken,
        bool isReferenceAdded)
    {
        var children = property.Children;
        var containsChild = children.Any(c => ReferenceEquals(c.Subject, childSubject));

        // For add: skip if already contains; for delete: need to find the key
        if (isReferenceAdded && containsChild)
        {
            return;
        }

        if (!isReferenceAdded)
        {
            if (property.GetValue() is not IDictionary dictionary)
            {
                return;
            }

            object? childKey = null;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (ReferenceEquals(entry.Value, childSubject))
                {
                    childKey = entry.Key;
                    break;
                }
            }

            if (childKey is null)
            {
                return; // Child not in this dictionary
            }

            var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
            if (propertyName is null)
            {
                return;
            }

            var containerNodeId = await OpcUaHelper.FindChildNodeIdAsync(
                session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);

            if (containerNodeId is null)
            {
                return;
            }

            var childInContainer = await FindChildInContainerAsync(
                containerNodeId, childNodeId, session, cancellationToken).ConfigureAwait(false);

            if (childInContainer is null)
            {
                // Child no longer in this container - remove it from the local dictionary
                if (_graphChangeApplier.RemoveFromDictionary(property, childKey, _source))
                {
                    _logger.LogDebug(
                        "ReferenceDeleted: Removed {ChildType} from dictionary {ParentType}.{Property}[{Key}]",
                        childSubject.GetType().Name,
                        trackedSubject.GetType().Name,
                        property.Name,
                        childKey);
                }
            }
        }
        else
        {
            var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
            if (propertyName is null)
            {
                return;
            }

            var containerNodeId = await OpcUaHelper.FindChildNodeIdAsync(
                session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);

            if (containerNodeId is null)
            {
                return;
            }

            var childInContainer = await FindChildInContainerAsync(
                containerNodeId, childNodeId, session, cancellationToken).ConfigureAwait(false);

            if (childInContainer is not null)
            {
                var dictionaryKey = childInContainer.BrowseName.Name;
                if (dictionaryKey is not null)
                {
                    // Add to dictionary - we already have the subject, use factory that returns it directly
                    var addedSubject = await _graphChangeApplier.AddToDictionaryAsync(
                        property,
                        dictionaryKey,
                        () => Task.FromResult(childSubject),
                        _source).ConfigureAwait(false);

                    if (addedSubject is not null)
                    {
                        _logger.LogDebug(
                            "ReferenceAdded: Added {ChildType} to dictionary {ParentType}.{Property}[{Key}]",
                            childSubject.GetType().Name,
                            trackedSubject.GetType().Name,
                            property.Name,
                            dictionaryKey);
                    }
                }
            }
        }
    }
}

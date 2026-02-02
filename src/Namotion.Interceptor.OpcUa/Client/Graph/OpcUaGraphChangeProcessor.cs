using System.Collections;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Graph;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Graph;

/// <summary>
/// Processes OPC UA node changes (from ModelChangeEvents or periodic resync) to update the C# model.
/// Compares remote address space with local model and creates/removes subjects as needed.
/// </summary>
internal class OpcUaGraphChangeProcessor
{
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

    // Track whether we're currently processing a remote ModelChangeEvent
    // When true, subject detachments should NOT trigger DeleteNodes calls back to the server
    private volatile bool _isProcessingRemoteChange;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpcUaGraphChangeProcessor"/> class.
    /// </summary>
    /// <param name="source">The OPC UA client source.</param>
    /// <param name="configuration">The client configuration.</param>
    /// <param name="subjectLoader">The subject loader for creating new subjects.</param>
    /// <param name="logger">The logger.</param>
    public OpcUaGraphChangeProcessor(
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
    /// Gets whether the processor is currently handling a remote change (ModelChangeEvent).
    /// When true, subject detachments should NOT trigger DeleteNodes calls back to the server
    /// because the deletion originated from the server.
    /// </summary>
    public bool IsProcessingRemoteChange => _isProcessingRemoteChange;

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
                        var containerNodeId = await OpcUaBrowseHelper.FindChildNodeIdAsync(session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
                        if (containerNodeId is not null)
                        {
                            await ProcessCollectionNodeChangesAsync(property, containerNodeId, session, cancellationToken).ConfigureAwait(false);
                        }
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

        _logger.LogDebug("Collection sync for '{Property}': remote={Remote}, local={Local}, toAdd={Add}, toRemove={Remove}",
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
                var newSubject = await _configuration.SubjectFactory.CreateSubjectForPropertyAsync(
                    property, remoteChild, session, cancellationToken).ConfigureAwait(false);

                var nodeId = ExpandedNodeId.ToNodeId(remoteChild.NodeId, session.NamespaceUris);
                RegisterSubjectNodeId(newSubject, nodeId);

                // Add to collection FIRST - this attaches the subject to the parent which registers it
                // The subject must be registered before LoadSubjectAsync can set up monitored items
                var currentCollection = property.GetValue() as IEnumerable<IInterceptorSubject?>;
                if (currentCollection is null)
                {
                    _logger.LogWarning(
                        "Cannot add to collection property '{PropertyName}': value is not a collection.",
                        property.Name);
                    continue;
                }

                var newCollection = DefaultSubjectFactory.Instance.AppendSubjectsToCollection(currentCollection, newSubject);
                property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newCollection);

                // Now load and set up monitoring - subject is now registered
                var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                    newSubject, remoteChild, session, cancellationToken).ConfigureAwait(false);

                // Read values first to get the correct current state
                await _source.ReadAndApplySubjectValuesAsync(newSubject, session, cancellationToken).ConfigureAwait(false);

                // Only add monitored items if not processing a remote change
                // to avoid stale subscription initial values overwriting the correct values
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
        }

        // Process removals
        if (indicesToRemove.Count > 0)
        {
            var currentCollectionForRemoval = property.GetValue() as IEnumerable<IInterceptorSubject?>;
            if (currentCollectionForRemoval is null)
            {
                _logger.LogWarning(
                    "Could not remove {RemovedCount} subjects from collection property '{PropertyName}': value is not a collection.",
                    indicesToRemove.Count, property.Name);
            }
            else
            {
                var newCollectionAfterRemoval = DefaultSubjectFactory.Instance.RemoveSubjectsFromCollection(
                    currentCollectionForRemoval, indicesToRemove.ToArray());
                property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newCollectionAfterRemoval);
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

        // Check if this index already exists in the local collection
        var localChildren = property.Children.ToList();
        var localIndices = new HashSet<int>(localChildren.Where(c => c.Index is int).Select(c => (int)c.Index!));
        if (localIndices.Contains(index))
        {
            return;
        }

        // Skip re-adding recently deleted items
        if (_configuration.EnableRemoteNodeManagement && WasRecentlyDeleted(nodeId))
        {
            return;
        }

        // Create the subject for this node
        var newSubject = await _configuration.SubjectFactory.CreateSubjectForPropertyAsync(
            property, nodeDetails, session, cancellationToken).ConfigureAwait(false);

        RegisterSubjectNodeId(newSubject, nodeId);

        // Add to collection - this attaches the subject to the parent which registers it
        var currentCollection = property.GetValue() as IEnumerable<IInterceptorSubject?>;
        if (currentCollection is null)
        {
            _logger.LogWarning(
                "ProcessCollectionItemAddedAsync: Cannot add to collection property '{PropertyName}': value is not a collection.",
                property.Name);
            return;
        }

        var newCollection = DefaultSubjectFactory.Instance.AppendSubjectsToCollection(currentCollection, newSubject);
        property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newCollection);

        // Now load and set up monitoring - subject is now registered
        var monitoredItems = await _subjectLoader.LoadSubjectAsync(
            newSubject, nodeDetails, session, cancellationToken).ConfigureAwait(false);

        // Read values first to get the correct current state
        await _source.ReadAndApplySubjectValuesAsync(newSubject, session, cancellationToken).ConfigureAwait(false);

        // Only add monitored items if not processing a remote change
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
                var newSubject = await _configuration.SubjectFactory.CreateSubjectForPropertyAsync(
                    property, remoteChild, session, cancellationToken).ConfigureAwait(false);

                var nodeId = ExpandedNodeId.ToNodeId(remoteChild.NodeId, session.NamespaceUris);
                RegisterSubjectNodeId(newSubject, nodeId);

                // Add to dictionary FIRST - this attaches the subject to the parent which registers it
                // The subject must be registered before LoadSubjectAsync can set up monitored items
                var currentDictionary = property.GetValue() as IDictionary;
                if (currentDictionary is null)
                {
                    _logger.LogWarning(
                        "Cannot add to dictionary property '{PropertyName}': value is not a dictionary.",
                        property.Name);
                    continue;
                }

                var newDictionary = DefaultSubjectFactory.Instance.AppendEntriesToDictionary(
                    currentDictionary, new KeyValuePair<object, IInterceptorSubject>(key, newSubject));
                property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newDictionary);
                localChildren = property.Children.ToDictionary(c => c.Index?.ToString() ?? "", c => c.Subject);

                // Now load and set up monitoring - subject is now registered
                var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                    newSubject, remoteChild, session, cancellationToken).ConfigureAwait(false);

                // Read values first to get the correct current state
                await _source.ReadAndApplySubjectValuesAsync(newSubject, session, cancellationToken).ConfigureAwait(false);

                // Only add monitored items if not processing a remote change
                // to avoid stale subscription initial values overwriting the correct values
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
        }

        // Process removals
        if (keysToRemove.Count > 0)
        {
            var currentDictionaryForRemoval = property.GetValue() as IDictionary;
            if (currentDictionaryForRemoval is null)
            {
                _logger.LogWarning(
                    "Could not remove {RemovedCount} entries from dictionary property '{PropertyName}': value is not a dictionary.",
                    keysToRemove.Count, property.Name);
            }
            else
            {
                var newDictionaryAfterRemoval = DefaultSubjectFactory.Instance.RemoveEntriesFromDictionary(
                    currentDictionaryForRemoval, keysToRemove.Cast<object>().ToArray());
                property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newDictionaryAfterRemoval);
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
                UnregisterSubjectNodeId(localNodeId);
            }

            // Skip re-adding recently deleted items
            if (_configuration.EnableRemoteNodeManagement && WasRecentlyDeleted(remoteNodeId!))
            {
                property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, null);
                return;
            }

            // Create new subject for the replacement
            var newSubject = await _configuration.SubjectFactory.CreateSubjectForPropertyAsync(
                property, referenceNode!, session, cancellationToken).ConfigureAwait(false);

            RegisterSubjectNodeId(newSubject, remoteNodeId!);

            // Set property value - this replaces the old subject and attaches the new one
            property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newSubject);

            // Now load and set up monitoring - subject is now registered
            var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                newSubject, referenceNode!, session, cancellationToken).ConfigureAwait(false);

            // Read values first to get the correct current state
            try
            {
                await _source.ReadAndApplySubjectValuesAsync(newSubject, session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read initial values for replaced reference property '{PropertyName}'.", property.Name);
            }

            // IMPORTANT: Do NOT add monitored items here when processing remote changes.
            // The subscription's "initial values" arrive asynchronously and would overwrite
            // the correct values we just read. Future updates will come through ModelChangeEvents.
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
        else if (hasRemoteValue && !hasLocalValue)
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
            var newSubject = await _configuration.SubjectFactory.CreateSubjectForPropertyAsync(
                property, referenceNode!, session, cancellationToken).ConfigureAwait(false);

            var nodeId = ExpandedNodeId.ToNodeId(referenceNode!.NodeId, session.NamespaceUris);
            RegisterSubjectNodeId(newSubject, nodeId);

            // Set property value FIRST - this attaches the subject to the parent which registers it
            property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newSubject);

            // Now load and set up monitoring - subject is now registered
            var monitoredItems = await _subjectLoader.LoadSubjectAsync(
                newSubject, referenceNode!, session, cancellationToken).ConfigureAwait(false);

            // Read values first to get the correct current state
            try
            {
                await _source.ReadAndApplySubjectValuesAsync(newSubject, session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read initial values for reference property '{PropertyName}'.", property.Name);
            }

            // IMPORTANT: Do NOT add monitored items here when processing remote changes.
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
        else if (!hasRemoteValue && hasLocalValue)
        {
            // Remote has no value but local does - clear local
            if (_source.TryGetSubjectNodeId(localSubject!, out var oldNodeId) && oldNodeId is not null)
            {
                UnregisterSubjectNodeId(oldNodeId);
            }
            property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, null);
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
        _logger.LogDebug("ProcessNodeAdded: Processing NodeId {NodeId}.", nodeId);

        var nodeDetails = await OpcUaBrowseHelper.ReadNodeDetailsAsync(session, nodeId, cancellationToken).ConfigureAwait(false);
        if (nodeDetails is null)
        {
            _logger.LogDebug("ProcessNodeAdded: Could not read node details for {NodeId}.", nodeId);
            return;
        }

        var directParentNodeId = await OpcUaBrowseHelper.FindParentNodeIdAsync(session, nodeId, cancellationToken).ConfigureAwait(false);
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
            if (property.IsSubjectCollection && OpcUaBrowseHelper.TryParseCollectionIndex(browseName, propertyName, out var parsedIndex))
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
                    var containerNodeId = await OpcUaBrowseHelper.FindChildNodeIdAsync(session, parentSubjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
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

        var parentProperty = parents[0].Property;

        // Determine the type of parent property and remove accordingly
        if (parentProperty.IsSubjectReference)
        {
            // Clear the reference property
            parentProperty.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, null);
        }
        else if (parentProperty.IsSubjectCollection)
        {
            // Capture existing subjects with their indices before removal
            // We need this to update NodeId registrations after reindexing
            var subjectsToReindex = new List<(IInterceptorSubject Subject, int OldIndex)>();
            var children = parentProperty.Children;
            int? removedIndex = null;

            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i];
                if (child.Index is not int idx)
                {
                    continue;
                }

                if (ReferenceEquals(child.Subject, deletedSubject))
                {
                    removedIndex = idx;
                }
                else if (child.Subject is not null)
                {
                    subjectsToReindex.Add((child.Subject, idx));
                }
            }

            if (removedIndex is int removedIdx)
            {
                var currentCollectionValue = parentProperty.GetValue() as IEnumerable<IInterceptorSubject?>;
                if (currentCollectionValue is not null)
                {
                    var newCollectionValue = DefaultSubjectFactory.Instance.RemoveSubjectsFromCollection(
                        currentCollectionValue, removedIdx);
                    parentProperty.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newCollectionValue);

                    // Update NodeId registrations for items that got reindexed
                    UpdateCollectionNodeIdRegistrationsAfterRemoval(parentProperty, subjectsToReindex, removedIdx);
                }
                else
                {
                    _logger.LogWarning(
                        "ProcessNodeDeleted: Could not remove subject from collection property '{PropertyName}': value is not a collection.",
                        parentProperty.Name);
                }
            }
        }
        else if (parentProperty.IsSubjectDictionary)
        {
            // Remove from dictionary
            if (parentProperty.GetValue() is IDictionary dictionary)
            {
                object? keyToRemove = null;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (ReferenceEquals(entry.Value, deletedSubject))
                    {
                        keyToRemove = entry.Key;
                        break;
                    }
                }

                if (keyToRemove is not null)
                {
                    var newDictionaryValue = DefaultSubjectFactory.Instance.RemoveEntriesFromDictionary(
                        dictionary, keyToRemove);
                    parentProperty.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newDictionaryValue);
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
        RegisteredSubjectProperty property,
        List<(IInterceptorSubject Subject, int OldIndex)> subjectsWithOldIndices,
        int removedIndex)
    {
        lock (_nodeIdToSubjectLock)
        {
            foreach (var (subject, oldIndex) in subjectsWithOldIndices)
            {
                // Only items after the removed index need reindexing
                if (oldIndex <= removedIndex)
                {
                    continue;
                }

                var newIndex = oldIndex - 1;

                if (_source.TryGetSubjectNodeId(subject, out var existingNodeId) && existingNodeId is not null)
                {
                    // Remove old registration
                    _nodeIdToSubject.Remove(existingNodeId);

                    // Construct new NodeId by replacing the index in the string representation
                    var existingNodeIdStr = existingNodeId.ToString();
                    var indexPattern = $"[{oldIndex}]";
                    var newIndexPattern = $"[{newIndex}]";
                    if (existingNodeIdStr.Contains(indexPattern))
                    {
                        var newNodeIdStr = existingNodeIdStr.Replace(indexPattern, newIndexPattern);
                        var newNodeId = new NodeId(newNodeIdStr);

                        // Register with new NodeId
                        _nodeIdToSubject[newNodeId] = subject;

                        // Update the subject's stored NodeId
                        _source.SetSubjectNodeId(subject, newNodeId);
                    }
                }
            }
        }
    }

    private async Task ProcessReferenceAddedAsync(NodeId childNodeId, ISession session, CancellationToken cancellationToken)
    {
        _logger.LogDebug("ProcessReferenceAddedAsync: Processing ReferenceAdded for NodeId {NodeId}", childNodeId);

        // Only handle if we track this child subject
        IInterceptorSubject? childSubject;
        lock (_nodeIdToSubjectLock)
        {
            if (!_nodeIdToSubject.TryGetValue(childNodeId, out childSubject))
            {
                _logger.LogDebug("ProcessReferenceAddedAsync: NodeId {NodeId} not found in tracked subjects", childNodeId);
                return;
            }
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
        IInterceptorSubject? childSubject;
        lock (_nodeIdToSubjectLock)
        {
            if (!_nodeIdToSubject.TryGetValue(childNodeId, out childSubject))
            {
                _logger.LogDebug("ProcessReferenceDeleted: NodeId {NodeId} not found in tracked subjects", childNodeId);
                return;
            }
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

            var childRefNodeId = await OpcUaBrowseHelper.FindChildNodeIdAsync(
                session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);

            if (childRefNodeId is not null && childRefNodeId.Equals(childNodeId))
            {
                var now = DateTimeOffset.UtcNow;
                property.SetValueFromSource(_source, now, now, childSubject);
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

            var refNodeId = await OpcUaBrowseHelper.FindChildNodeIdAsync(
                session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);

            if (refNodeId is null || !refNodeId.Equals(childNodeId))
            {
                // Reference no longer exists on server - remove it
                var now = DateTimeOffset.UtcNow;
                property.SetValueFromSource(_source, now, now, null);
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

        return await OpcUaBrowseHelper.FindChildNodeIdAsync(
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
        var containerChildren = await OpcUaBrowseHelper.BrowseNodeAsync(
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
                // Add to collection
                var currentCollection = property.GetValue() as IEnumerable<IInterceptorSubject?>;
                if (currentCollection is not null)
                {
                    var newCollection = DefaultSubjectFactory.Instance.AppendSubjectsToCollection(
                        currentCollection, childSubject);
                    property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newCollection);
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
                for (var i = 0; i < children.Length; i++)
                {
                    if (ReferenceEquals(children[i].Subject, childSubject) && children[i].Index is int index)
                    {
                        var currentCollection = property.GetValue() as IEnumerable<IInterceptorSubject?>;
                        if (currentCollection is not null)
                        {
                            var newCollection = DefaultSubjectFactory.Instance.RemoveSubjectsFromCollection(
                                currentCollection, index);
                            property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newCollection);
                            _logger.LogDebug(
                                "ReferenceDeleted: Removed {ChildType} from collection {ParentType}.{Property}",
                                childSubject.GetType().Name,
                                trackedSubject.GetType().Name,
                                property.Name);
                        }
                        break;
                    }
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

            var containerNodeId = await OpcUaBrowseHelper.FindChildNodeIdAsync(
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
                var newDictionary = DefaultSubjectFactory.Instance.RemoveEntriesFromDictionary(
                    dictionary, childKey);
                property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newDictionary);
                _logger.LogDebug(
                    "ReferenceDeleted: Removed {ChildType} from dictionary {ParentType}.{Property}[{Key}]",
                    childSubject.GetType().Name,
                    trackedSubject.GetType().Name,
                    property.Name,
                    childKey);
            }
        }
        else
        {
            var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
            if (propertyName is null)
            {
                return;
            }

            var containerNodeId = await OpcUaBrowseHelper.FindChildNodeIdAsync(
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
                var currentDictionary = property.GetValue() as IDictionary;
                if (currentDictionary is not null && dictionaryKey is not null)
                {
                    var newDictionary = DefaultSubjectFactory.Instance.AppendEntriesToDictionary(
                        currentDictionary,
                        new KeyValuePair<object, IInterceptorSubject>(dictionaryKey, childSubject));
                    property.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, newDictionary);
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

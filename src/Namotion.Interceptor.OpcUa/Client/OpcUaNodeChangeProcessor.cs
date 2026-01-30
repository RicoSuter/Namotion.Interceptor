using Microsoft.Extensions.Logging;
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
    private readonly OpcUaSubjectClientSource _source;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly ILogger _logger;

    // Maps NodeId to subject for reverse lookup when processing node deletions
    private readonly Dictionary<NodeId, IInterceptorSubject> _nodeIdToSubject = new();
    private readonly Lock _nodeIdToSubjectLock = new();

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
                    var containerNodeId = await FindChildNodeIdAsync(session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
                    if (containerNodeId is not null)
                    {
                        await ProcessCollectionNodeChangesAsync(property, containerNodeId, session, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (property.IsSubjectDictionary)
                {
                    var containerNodeId = await FindChildNodeIdAsync(session, subjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
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
                    if (_source.WasRecentlyDeleted(nodeId))
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

                // Load and track subject BEFORE adding to collection - ensures subject is fully tracked
                // before it becomes removable (prevents race condition with user removing subject)
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

                // Now add to collection - subject is already fully tracked
                AddToCollectionProperty(property, localChildren, index, newSubject);
                localChildren = property.Children.ToList();

                await _source.ReadAndApplySubjectValuesAsync(newSubject, session, cancellationToken).ConfigureAwait(false);
            }
        }

        // Process removals
        if (indicesToRemove.Count > 0)
        {
            RemoveFromCollectionProperty(property, indicesToRemove);
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
                    if (_source.WasRecentlyDeleted(nodeId))
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

                // Load and track subject BEFORE adding to dictionary
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

                // Now add to dictionary - subject is already fully tracked
                AddToDictionaryProperty(property, localChildren, key, newSubject);
                localChildren = property.Children.ToDictionary(c => c.Index?.ToString() ?? "", c => c.Subject);

                await _source.ReadAndApplySubjectValuesAsync(newSubject, session, cancellationToken).ConfigureAwait(false);
            }
        }

        // Process removals
        if (keysToRemove.Count > 0)
        {
            RemoveFromDictionaryProperty(property, keysToRemove);
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
                if (_source.WasRecentlyDeleted(referenceNodeId))
                {
                    return;
                }
            }

            // Remote has value but local is null - create local subject
            var newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(
                property, referenceNode!, session, cancellationToken).ConfigureAwait(false);

            var nodeId = ExpandedNodeId.ToNodeId(referenceNode!.NodeId, session.NamespaceUris);
            RegisterSubjectNodeId(newSubject, nodeId);

            // Load and track subject BEFORE setting property value
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

            // Now set the property value - subject is already fully tracked
            property.SetValue(newSubject);

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
            property.SetValue(null);
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
        var nodeDetails = await BrowseNodeDetailsAsync(session, nodeId, cancellationToken).ConfigureAwait(false);
        if (nodeDetails is null)
        {
            return;
        }

        var directParentNodeId = await FindParentNodeIdAsync(session, nodeId, cancellationToken).ConfigureAwait(false);
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

            var nextParentNodeId = await FindParentNodeIdAsync(session, currentNodeId, cancellationToken).ConfigureAwait(false);
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
            if (!property.IsSubjectCollection && !property.IsSubjectDictionary)
            {
                continue;
            }

            var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
            if (propertyName is null)
            {
                continue;
            }

            // Check if this is a collection item (pattern: PropertyName[index])
            if (property.IsSubjectCollection && TryParseCollectionIndex(browseName, propertyName, out _))
            {
                if (_source.TryGetSubjectNodeId(parentSubject, out var parentSubjectNodeId) && parentSubjectNodeId is not null)
                {
                    var containerNodeId = await FindChildNodeIdAsync(session, parentSubjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
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
                    var containerNodeId = await FindChildNodeIdAsync(session, parentSubjectNodeId, propertyName, cancellationToken).ConfigureAwait(false);
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
        lock (_nodeIdToSubjectLock)
        {
            _nodeIdToSubject.Remove(nodeId);
        }
    }

    private Task ProcessReferenceAddedAsync(NodeId nodeId, ISession session, CancellationToken cancellationToken)
    {
        // Reference events often accompany NodeAdded - rely on that for structural changes
        return Task.CompletedTask;
    }

    private void ProcessReferenceDeleted(NodeId nodeId)
    {
        // Reference events often accompany NodeDeleted - rely on that for structural changes
    }

    private async Task<NodeId?> FindParentNodeIdAsync(ISession session, NodeId childNodeId, CancellationToken cancellationToken)
    {
        var browseDescription = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = childNodeId,
                BrowseDirection = BrowseDirection.Inverse,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
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
            var references = response.Results[0].References;
            if (references.Count > 0)
            {
                return ExpandedNodeId.ToNodeId(references[0].NodeId, session.NamespaceUris);
            }
        }

        return null;
    }

    private async Task<ReferenceDescription?> BrowseNodeDetailsAsync(ISession session, NodeId nodeId, CancellationToken cancellationToken)
    {
        // Read the node's attributes to construct a ReferenceDescription
        var readValues = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.BrowseName },
            new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.DisplayName },
            new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.NodeClass }
        };

        var response = await session.ReadAsync(
            null,
            0,
            TimestampsToReturn.Neither,
            readValues,
            cancellationToken).ConfigureAwait(false);

        if (response.Results.Count >= 3 &&
            StatusCode.IsGood(response.Results[0].StatusCode) &&
            StatusCode.IsGood(response.Results[2].StatusCode))
        {
            return new ReferenceDescription
            {
                NodeId = new ExpandedNodeId(nodeId),
                BrowseName = response.Results[0].Value as QualifiedName ?? new QualifiedName("Unknown"),
                DisplayName = response.Results[1].Value as LocalizedText ?? new LocalizedText("Unknown"),
                NodeClass = (NodeClass)(int)response.Results[2].Value
            };
        }

        return null;
    }

    private async Task<NodeId?> FindChildNodeIdAsync(ISession session, NodeId parentNodeId, string browseName, CancellationToken cancellationToken)
    {
        var children = await OpcUaBrowseHelper.BrowseNodeAsync(session, parentNodeId, cancellationToken).ConfigureAwait(false);
        foreach (var child in children)
        {
            if (child.BrowseName.Name == browseName)
            {
                return ExpandedNodeId.ToNodeId(child.NodeId, session.NamespaceUris);
            }
        }
        return null;
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

    private void UpdateCollectionProperty(
        RegisteredSubjectProperty property,
        List<SubjectPropertyChild> localChildren,
        List<(int Index, IInterceptorSubject Subject)> newSubjects)
    {
        try
        {
            // Get current array value
            var currentValue = property.GetValue();
            if (currentValue is not Array currentArray)
            {
                _logger.LogWarning(
                    "Cannot update collection property '{PropertyName}': value is not an array.",
                    property.Name);
                return;
            }

            var elementType = currentArray.GetType().GetElementType();
            if (elementType is null)
            {
                return;
            }

            // Create new array with existing + new subjects
            var newLength = localChildren.Count + newSubjects.Count;
            var newArray = Array.CreateInstance(elementType, newLength);

            // Copy existing elements
            for (var i = 0; i < localChildren.Count; i++)
            {
                newArray.SetValue(localChildren[i].Subject, i);
            }

            // Add new elements at their indices (simplified: append at end)
            var insertIndex = localChildren.Count;
            foreach (var (_, subject) in newSubjects.OrderBy(s => s.Index))
            {
                newArray.SetValue(subject, insertIndex++);
            }

            // Set the new array value
            property.SetValue(newArray);

            _logger.LogDebug(
                "Updated collection property '{PropertyName}' with {NewCount} new subjects. Total: {Total}",
                property.Name, newSubjects.Count, newLength);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update collection property '{PropertyName}'.", property.Name);
        }
    }

    private void AddToCollectionProperty(
        RegisteredSubjectProperty property,
        List<SubjectPropertyChild> localChildren,
        int index,
        IInterceptorSubject newSubject)
    {
        try
        {
            // Get current array value
            var currentValue = property.GetValue();
            if (currentValue is not Array currentArray)
            {
                _logger.LogWarning(
                    "Cannot add to collection property '{PropertyName}': value is not an array.",
                    property.Name);
                return;
            }

            var elementType = currentArray.GetType().GetElementType();
            if (elementType is null)
            {
                return;
            }

            // Create new array with space for the new item
            var newLength = currentArray.Length + 1;
            var newArray = Array.CreateInstance(elementType, newLength);

            // Copy existing elements
            for (var i = 0; i < currentArray.Length; i++)
            {
                newArray.SetValue(currentArray.GetValue(i), i);
            }

            // Add the new element at the end
            newArray.SetValue(newSubject, currentArray.Length);

            // Set the new array value (this attaches the subject and registers it)
            property.SetValue(newArray);

            _logger.LogDebug(
                "Added subject to collection property '{PropertyName}' at index {Index}. Total: {Total}",
                property.Name, index, newLength);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add to collection property '{PropertyName}'.", property.Name);
        }
    }

    private void RemoveFromCollectionProperty(
        RegisteredSubjectProperty property,
        List<int> indicesToRemove)
    {
        try
        {
            // Get current array value
            var currentValue = property.GetValue();
            if (currentValue is not Array currentArray)
            {
                return;
            }

            var elementType = currentArray.GetType().GetElementType();
            if (elementType is null)
            {
                return;
            }

            // Create set of indices to remove
            var removeSet = new HashSet<int>(indicesToRemove);

            // Create new array without removed elements
            var newLength = currentArray.Length - indicesToRemove.Count;
            if (newLength < 0) newLength = 0;

            var newArray = Array.CreateInstance(elementType, newLength);
            var newIndex = 0;

            for (var i = 0; i < currentArray.Length; i++)
            {
                if (!removeSet.Contains(i))
                {
                    newArray.SetValue(currentArray.GetValue(i), newIndex++);
                }
            }

            // Set the new array value
            property.SetValue(newArray);

            _logger.LogDebug(
                "Removed {RemovedCount} subjects from collection property '{PropertyName}'. Remaining: {Remaining}",
                indicesToRemove.Count, property.Name, newLength);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove from collection property '{PropertyName}'.", property.Name);
        }
    }

    private void UpdateDictionaryProperty(
        RegisteredSubjectProperty property,
        Dictionary<string, IInterceptorSubject> localChildren,
        List<(string Key, IInterceptorSubject Subject)> newSubjects)
    {
        try
        {
            // Get current dictionary value
            var currentValue = property.GetValue();
            if (currentValue is null)
            {
                _logger.LogWarning(
                    "Cannot update dictionary property '{PropertyName}': value is null.",
                    property.Name);
                return;
            }

            var dictType = currentValue.GetType();
            if (!dictType.IsGenericType || dictType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            {
                _logger.LogWarning(
                    "Cannot update dictionary property '{PropertyName}': value is not a Dictionary<,>.",
                    property.Name);
                return;
            }

            // Create new dictionary with existing + new entries
            var keyType = dictType.GetGenericArguments()[0];
            var valueType = dictType.GetGenericArguments()[1];
            var newDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
            var newDict = Activator.CreateInstance(newDictType) as System.Collections.IDictionary;

            if (newDict is null)
            {
                return;
            }

            // Copy existing entries
            foreach (var kvp in localChildren)
            {
                newDict[kvp.Key] = kvp.Value;
            }

            // Add new entries
            foreach (var (key, subject) in newSubjects)
            {
                newDict[key] = subject;
            }

            // Set the new dictionary value
            property.SetValue(newDict);

            _logger.LogDebug(
                "Updated dictionary property '{PropertyName}' with {NewCount} new entries. Total: {Total}",
                property.Name, newSubjects.Count, newDict.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update dictionary property '{PropertyName}'.", property.Name);
        }
    }

    private void AddToDictionaryProperty(
        RegisteredSubjectProperty property,
        Dictionary<string, IInterceptorSubject> localChildren,
        string key,
        IInterceptorSubject newSubject)
    {
        try
        {
            // Get current dictionary value
            var currentValue = property.GetValue();
            if (currentValue is null)
            {
                _logger.LogWarning(
                    "Cannot add to dictionary property '{PropertyName}': value is null.",
                    property.Name);
                return;
            }

            var dictType = currentValue.GetType();
            if (!dictType.IsGenericType || dictType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            {
                _logger.LogWarning(
                    "Cannot add to dictionary property '{PropertyName}': value is not a Dictionary<,>.",
                    property.Name);
                return;
            }

            // Create new dictionary with existing entries plus new one
            var keyType = dictType.GetGenericArguments()[0];
            var valueType = dictType.GetGenericArguments()[1];
            var newDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
            var newDict = Activator.CreateInstance(newDictType) as System.Collections.IDictionary;

            if (newDict is null)
            {
                return;
            }

            // Copy existing entries
            foreach (var kvp in localChildren)
            {
                newDict[kvp.Key] = kvp.Value;
            }

            // Add the new entry
            newDict[key] = newSubject;

            // Set the new dictionary value (this attaches the subject and registers it)
            property.SetValue(newDict);

            _logger.LogDebug(
                "Added subject to dictionary property '{PropertyName}' with key '{Key}'. Total: {Total}",
                property.Name, key, newDict.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add to dictionary property '{PropertyName}'.", property.Name);
        }
    }

    private void RemoveFromDictionaryProperty(
        RegisteredSubjectProperty property,
        List<string> keysToRemove)
    {
        try
        {
            // Get current dictionary value
            var currentValue = property.GetValue();
            if (currentValue is null)
            {
                return;
            }

            var dictType = currentValue.GetType();
            if (!dictType.IsGenericType || dictType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            {
                return;
            }

            // Cast to IDictionary to manipulate
            if (currentValue is not System.Collections.IDictionary currentDict)
            {
                return;
            }

            // Create new dictionary without removed keys
            var keyType = dictType.GetGenericArguments()[0];
            var valueType = dictType.GetGenericArguments()[1];
            var newDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
            var newDict = Activator.CreateInstance(newDictType) as System.Collections.IDictionary;

            if (newDict is null)
            {
                return;
            }

            var removeSet = new HashSet<string>(keysToRemove);

            foreach (System.Collections.DictionaryEntry entry in currentDict)
            {
                if (entry.Key is null)
                {
                    continue;
                }

                var key = entry.Key.ToString() ?? "";
                if (!removeSet.Contains(key))
                {
                    newDict[entry.Key] = entry.Value;
                }
            }

            // Set the new dictionary value
            property.SetValue(newDict);

            _logger.LogDebug(
                "Removed {RemovedCount} entries from dictionary property '{PropertyName}'. Remaining: {Remaining}",
                keysToRemove.Count, property.Name, newDict.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove from dictionary property '{PropertyName}'.", property.Name);
        }
    }
}

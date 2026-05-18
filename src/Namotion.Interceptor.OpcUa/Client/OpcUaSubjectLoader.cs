using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectLoader
{
    private const uint NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;
    private readonly OpcUaSubjectClientSource _source;

    public OpcUaSubjectLoader(
        OpcUaClientConfiguration configuration,
        SourceOwnershipManager ownership,
        OpcUaSubjectClientSource source,
        ILogger logger)
    {
        _configuration = configuration;
        _ownership = ownership;
        _source = source;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(
        IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        CancellationToken cancellationToken)
    {
        var monitoredItems = new List<MonitoredItem>();
        var loadedSubjects = new HashSet<IInterceptorSubject>();
        var subjectsByNodeId = new Dictionary<NodeId, IInterceptorSubject>();
        await LoadSubjectAsync(subject, node, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
        return monitoredItems;
    }

    private Task LoadSubjectAsync(IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        CancellationToken cancellationToken)
    {
        return LoadSubjectAsync(subject, node, session, monitoredItems, loadedSubjects, subjectsByNodeId, prefetchedBrowseResults: null, cancellationToken);
    }

    private async Task LoadSubjectAsync(IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        ReferenceDescriptionCollection? prefetchedBrowseResults,
        CancellationToken cancellationToken)
    {
        if (!loadedSubjects.Add(subject))
        {
            return;
        }

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        // Phase 1: Browse the current node's children (use prefetched results if available)
        var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, session.NamespaceUris);
        var nodeReferences = prefetchedBrowseResults
            ?? await BrowseNodeAsync(nodeId, session, cancellationToken).ConfigureAwait(false);
        var distinctReferences = DistinctByResolvedNodeId(nodeReferences, session);

        // Phase 2: Partition children - find matching properties per node, collect dynamic nodes for batch resolution
        // We store per-child classification so we can process in original order later.
        var childEntries = new List<(ReferenceDescription Reference, NodeId ResolvedNodeId, RegisteredSubjectProperty? Property, bool NeedsDynamicType)>(distinctReferences.Count);
        var dynamicObjectNodeIds = new List<(int Index, NodeId ResolvedNodeId)>();
        var dynamicVariableNodes = new List<(int Index, NodeId ResolvedNodeId, ReferenceDescription Reference)>();

        for (var i = 0; i < distinctReferences.Count; i++)
        {
            var (nodeReference, resolvedNodeId) = distinctReferences[i];

            var property = await FindSubjectPropertyAsync(registeredSubject, nodeReference, session, cancellationToken).ConfigureAwait(false);
            if (property is not null)
            {
                childEntries.Add((nodeReference, resolvedNodeId, property, false));
                continue;
            }

            // Check if this should be a dynamic property
            var dynamicPropertyName = nodeReference.BrowseName.Name;

            var propertyExists = false;
            foreach (var childProperty in registeredSubject.Properties)
            {
                if (childProperty.Name == dynamicPropertyName)
                {
                    propertyExists = true;
                    break;
                }
            }

            if (propertyExists)
            {
                childEntries.Add((nodeReference, resolvedNodeId, null, false));
                continue;
            }

            var addAsDynamic = _configuration.ShouldAddDynamicProperty is not null &&
                await _configuration.ShouldAddDynamicProperty(nodeReference, cancellationToken).ConfigureAwait(false);

            if (!addAsDynamic)
            {
                childEntries.Add((nodeReference, resolvedNodeId, null, false));
                continue;
            }

            // Collect for batch type resolution
            childEntries.Add((nodeReference, resolvedNodeId, null, true));
            if (nodeReference.NodeClass == NodeClass.Object)
            {
                dynamicObjectNodeIds.Add((i, resolvedNodeId));
            }
            else if (nodeReference.NodeClass == NodeClass.Variable)
            {
                dynamicVariableNodes.Add((i, resolvedNodeId, nodeReference));
            }
        }

        // Phase 3: Batch-resolve types for dynamic nodes
        // 3a: Batch-browse Object nodes to classify them (collection/dictionary/subject)
        var objectTypeMap = new Dictionary<NodeId, Type>();
        if (dynamicObjectNodeIds.Count > 0)
        {
            var objectNodeIds = dynamicObjectNodeIds.Select(o => o.ResolvedNodeId).ToList();
            var objectBrowseResults = await BrowseManyNodesAsync(objectNodeIds, session, cancellationToken).ConfigureAwait(false);
            foreach (var (_, resolvedNodeId) in dynamicObjectNodeIds)
            {
                var children = objectBrowseResults.TryGetValue(resolvedNodeId, out var c)
                    ? c
                    : new ReferenceDescriptionCollection();
                objectTypeMap[resolvedNodeId] = OpcUaTypeResolver.ClassifyObjectNode(children);
            }
        }

        // 3b: Batch-resolve Variable types via ResolveVariableTypesAsync
        var variableTypeMap = new Dictionary<NodeId, Type?>();
        if (dynamicVariableNodes.Count > 0)
        {
            var variableInputs = dynamicVariableNodes
                .Select(v => (v.ResolvedNodeId, v.Reference))
                .ToList();
            variableTypeMap = await _configuration.TypeResolver.ResolveVariableTypesAsync(session, variableInputs, cancellationToken).ConfigureAwait(false);
        }

        // Phase 4: Process all children in original browse order
        var attributeVariableNodes = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var pendingSubjectRefs = new List<(RegisteredSubjectProperty Property, ReferenceDescription NodeReference, NodeId ResolvedNodeId, IInterceptorSubject SubjectToLoad, bool IsNew)>();

        for (var i = 0; i < childEntries.Count; i++)
        {
            var (nodeReference, resolvedNodeId, property, needsDynamicType) = childEntries[i];

            if (needsDynamicType && property is null)
            {
                Type? inferredType = null;
                if (nodeReference.NodeClass == NodeClass.Object)
                {
                    objectTypeMap.TryGetValue(resolvedNodeId, out inferredType);
                }
                else if (nodeReference.NodeClass == NodeClass.Variable)
                {
                    variableTypeMap.TryGetValue(resolvedNodeId, out inferredType);
                }

                if (inferredType is null)
                {
                    _logger.LogWarning(
                        "Could not infer type for dynamic property '{PropertyName}' (NodeId: {NodeId}). Skipping property.",
                        nodeReference.BrowseName.Name, nodeReference.NodeId);
                    continue;
                }

                object? value = null;
                property = registeredSubject.AddProperty(
                    nodeReference.BrowseName.Name,
                    inferredType,
                    _ => value,
                    (_, o) => value = o,
                    _configuration.TypeResolver.GetDynamicPropertyAttributes(nodeReference, session));
            }

            if (property is null)
            {
                continue;
            }

            var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
            if (propertyName is not null)
            {
                if (property.IsSubjectReference)
                {
                    var nodeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(property);
                    if (nodeConfiguration?.NodeClass == Mapping.OpcUaNodeClass.Variable)
                    {
                        await LoadVariableNodeForSubjectAsync(property, resolvedNodeId, session, monitoredItems, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        // Prepare subject reference for batched loading
                        if (subjectsByNodeId.TryGetValue(resolvedNodeId, out var reusedSubject))
                        {
                            property.SetValueFromSource(_source, null, null, reusedSubject);
                        }
                        else
                        {
                            var existingChildren = property.Children;
                            var existingChild = existingChildren.IsEmpty ? default : existingChildren[0];
                            var isNewSubject = existingChild.Subject is null;
                            var subjectToLoad = existingChild.Subject
                                ?? await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeReference, session, cancellationToken).ConfigureAwait(false);

                            if (isNewSubject)
                            {
                                subjectToLoad.Context.AddFallbackContext(subject.Context);
                            }

                            subjectsByNodeId.TryAdd(resolvedNodeId, subjectToLoad);
                            pendingSubjectRefs.Add((property, nodeReference, resolvedNodeId, subjectToLoad, isNewSubject));
                        }
                    }
                }
                else if (property.IsSubjectCollection)
                {
                    await LoadSubjectCollectionAsync(property, resolvedNodeId, monitoredItems, session, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
                }
                else if (property.IsSubjectDictionary)
                {
                    await LoadSubjectDictionaryAsync(property, resolvedNodeId, monitoredItems, session, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    MonitorValueNode(resolvedNodeId, property, monitoredItems);
                    attributeVariableNodes.Add((property, resolvedNodeId));
                }
            }
        }

        // Phase 4b: Batch-load all collected subject references
        if (pendingSubjectRefs.Count > 0)
        {
            var subjectsToLoad = pendingSubjectRefs
                .Select(r => (r.NodeReference, r.SubjectToLoad))
                .ToList();
            await LoadManySubjectsAsync(subjectsToLoad, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);

            foreach (var (property, _, _, subjectToLoad, isNew) in pendingSubjectRefs)
            {
                if (isNew)
                {
                    property.SetValueFromSource(_source, null, null, subjectToLoad);
                }
            }
        }

        // Phase 5: Batch attribute browsing for all monitored variable nodes
        await LoadAttributeNodesForManyAsync(attributeVariableNodes, session, monitoredItems, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadAttributeNodesForManyAsync(
        IReadOnlyList<(RegisteredSubjectProperty Property, NodeId NodeId)> variableNodes,
        ISession session,
        List<MonitoredItem> monitoredItems,
        CancellationToken cancellationToken)
    {
        if (variableNodes.Count == 0)
        {
            return;
        }

        var visitedNodes = new HashSet<NodeId>();

        // Iterative approach: process one round of attribute nesting at a time
        // Current round starts with the variable nodes passed in
        var currentRound = new List<(RegisteredSubjectProperty Property, NodeId ParentNodeId)>(variableNodes);

        while (currentRound.Count > 0)
        {
            // Mark all current round nodes as visited
            var nodesToBrowse = new List<NodeId>();
            var browseableEntries = new List<(RegisteredSubjectProperty Property, NodeId ParentNodeId)>();
            foreach (var (property, parentNodeId) in currentRound)
            {
                if (visitedNodes.Add(parentNodeId))
                {
                    nodesToBrowse.Add(parentNodeId);
                    browseableEntries.Add((property, parentNodeId));
                }
            }

            if (nodesToBrowse.Count == 0)
            {
                break;
            }

            // Batch-browse all nodes in this round
            var browseResults = await BrowseManyNodesAsync(nodesToBrowse, session, cancellationToken).ConfigureAwait(false);

            // Collect dynamic attribute Variable children for batch type resolution
            var dynamicVariableNodes = new List<(RegisteredSubjectProperty OwnerProperty, NodeId ChildNodeId, ReferenceDescription ChildNode, string BrowseName)>();

            // Track entries that need recursive attribute nesting
            var nextRound = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();

            foreach (var (property, parentNodeId) in browseableEntries)
            {
                if (!browseResults.TryGetValue(parentNodeId, out var rawChildren) || rawChildren.Count == 0)
                {
                    continue;
                }

                var childNodes = DistinctByResolvedNodeId(rawChildren, session);
                var processedBrowseNames = new HashSet<string>();

                // First pass: match known attributes from C# model
                foreach (var attribute in property.Attributes)
                {
                    var attributeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(attribute);
                    if (attributeConfiguration is null)
                    {
                        continue;
                    }

                    var attributeBrowseName = attributeConfiguration.BrowseName ?? attribute.BrowseName;
                    NodeId? matchingNodeId = null;
                    foreach (var (childNode, childNodeId) in childNodes)
                    {
                        if (childNode.BrowseName.Name == attributeBrowseName)
                        {
                            matchingNodeId = childNodeId;
                            break;
                        }
                    }

                    if (matchingNodeId is null)
                    {
                        continue;
                    }

                    processedBrowseNames.Add(attributeBrowseName);
                    MonitorValueNode(matchingNodeId, attribute, monitoredItems);

                    // Queue for recursive attribute nesting in next round
                    nextRound.Add((attribute, matchingNodeId));
                }

                // Second pass: collect dynamic attributes for batch type resolution
                foreach (var (childNode, childNodeId) in childNodes)
                {
                    if (childNode.NodeClass != NodeClass.Variable)
                    {
                        continue;
                    }

                    var browseName = childNode.BrowseName.Name;
                    if (!processedBrowseNames.Add(browseName))
                    {
                        continue;
                    }

                    if (property.TryGetAttribute(browseName) is not null)
                    {
                        _logger.LogWarning(
                            "Skipping OPC UA child '{AttributeName}' on '{PropertyName}' (parent {ParentNodeId}, child {ChildNodeId}): an attribute with this name was already registered by another source (e.g. a lifecycle handler).",
                            browseName, property.Name, parentNodeId, childNode.NodeId);
                        continue;
                    }

                    var addAsDynamic = _configuration.ShouldAddDynamicAttribute is not null &&
                        await _configuration.ShouldAddDynamicAttribute(childNode, cancellationToken).ConfigureAwait(false);
                    if (!addAsDynamic)
                    {
                        continue;
                    }

                    dynamicVariableNodes.Add((property, childNodeId, childNode, browseName));
                }
            }

            // Batch-resolve types for dynamic attribute Variables
            if (dynamicVariableNodes.Count > 0)
            {
                var variableInputs = dynamicVariableNodes
                    .Select(v => (v.ChildNodeId, v.ChildNode))
                    .ToList();
                var resolvedTypes = await _configuration.TypeResolver.ResolveVariableTypesAsync(session, variableInputs, cancellationToken).ConfigureAwait(false);

                foreach (var (ownerProperty, childNodeId, childNode, browseName) in dynamicVariableNodes)
                {
                    resolvedTypes.TryGetValue(childNodeId, out var inferredType);
                    if (inferredType is null)
                    {
                        _logger.LogWarning(
                            "Could not infer type for dynamic attribute '{AttributeName}' on property '{PropertyName}' (NodeId: {NodeId}). Skipping attribute.",
                            browseName, ownerProperty.Name, childNode.NodeId);
                        continue;
                    }

                    object? value = null;
                    var dynamicAttribute = ownerProperty.AddAttribute(
                        browseName,
                        inferredType,
                        _ => value,
                        (_, o) => value = o,
                        _configuration.TypeResolver.GetDynamicPropertyAttributes(childNode, session));

                    MonitorValueNode(childNodeId, dynamicAttribute, monitoredItems);

                    // Queue for recursive attribute nesting in next round
                    nextRound.Add((dynamicAttribute, childNodeId));
                }
            }

            currentRound = nextRound;
        }
    }

    private async Task LoadSubjectReferenceAsync(RegisteredSubjectProperty property,
        ReferenceDescription nodeReference,
        NodeId nodeId,
        IInterceptorSubject subject,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        CancellationToken cancellationToken)
    {
        if (subjectsByNodeId.TryGetValue(nodeId, out var reusedSubject))
        {
            // The cache wins over property.Children: if a sibling reference loaded earlier in
            // this call already bound a subject to this NodeId, every other property pointing
            // at the same node must resolve to that same instance to preserve DAG identity.
            property.SetValueFromSource(_source, null, null, reusedSubject);
            return;
        }

        var existingChildren = property.Children;
        var existingChild = existingChildren.IsEmpty ? default : existingChildren[0];
        var isNewSubject = existingChild.Subject is null;
        var subjectToLoad = existingChild.Subject
            ?? await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeReference, session, cancellationToken).ConfigureAwait(false);

        if (isNewSubject)
        {
            subjectToLoad.Context.AddFallbackContext(subject.Context);
        }

        // Pre-attached children participate in the dedup cache too: any later sibling
        // resolving to the same NodeId will route to this instance via the cache-hit
        // branch above, rather than creating a parallel subject.
        subjectsByNodeId.TryAdd(nodeId, subjectToLoad);

        await LoadSubjectAsync(subjectToLoad, nodeReference, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);

        if (isNewSubject)
        {
            property.SetValueFromSource(_source, null, null, subjectToLoad);
        }
    }

    private async Task LoadSubjectCollectionAsync(RegisteredSubjectProperty property,
        NodeId childNodeId,
        List<MonitoredItem> monitoredItems,
        ISession session,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        CancellationToken cancellationToken)
    {
        var rawChildNodes = await BrowseNodeAsync(childNodeId, session, cancellationToken).ConfigureAwait(false);
        var childNodes = DistinctByResolvedNodeId(rawChildNodes, session);

        var childCount = childNodes.Count;
        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(childCount);

        var existingChildren = property.Children;

        for (var i = 0; i < childCount; i++)
        {
            var (childNode, nodeId) = childNodes[i];
            var childSubject = i < existingChildren.Length ? existingChildren[i].Subject : null;

            if (childSubject is null)
            {
                subjectsByNodeId.TryGetValue(nodeId, out childSubject);
            }

            childSubject ??= await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                property, childNode, i, session, cancellationToken).ConfigureAwait(false);

            subjectsByNodeId.TryAdd(nodeId, childSubject);

            children.Add((childNode, childSubject));
        }

        var collection = DefaultSubjectFactory.Instance
            .CreateSubjectCollection(property.Type, children.Select(c => c.Subject));

        property.SetValueFromSource(_source, null, null, collection);

        await LoadManySubjectsAsync(children, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadSubjectDictionaryAsync(
        RegisteredSubjectProperty property,
        NodeId childNodeId,
        List<MonitoredItem> monitoredItems,
        ISession session,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        CancellationToken cancellationToken)
    {
        var rawChildNodes = await BrowseNodeAsync(childNodeId, session, cancellationToken).ConfigureAwait(false);
        var childNodes = DistinctByResolvedNodeId(rawChildNodes, session);
        var existingChildren = property.Children.ToDictionary(c => c.Index!, c => c.Subject);
        var entries = new Dictionary<object, IInterceptorSubject>();
        var nodesByKey = new Dictionary<object, ReferenceDescription>();

        foreach (var (childNode, nodeId) in childNodes)
        {
            var key = childNode.BrowseName.Name; // Use BrowseName as dictionary key
            var childSubject = existingChildren.GetValueOrDefault(key);

            if (childSubject is null)
            {
                subjectsByNodeId.TryGetValue(nodeId, out childSubject);
            }

            childSubject ??= await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                property, childNode, key, session, cancellationToken).ConfigureAwait(false);

            subjectsByNodeId.TryAdd(nodeId, childSubject);

            entries[key] = childSubject;
            nodesByKey[key] = childNode;
        }

        var dictionary = DefaultSubjectFactory.Instance.CreateSubjectDictionary(property.Type, entries);
        property.SetValueFromSource(_source, null, null, dictionary);

        var dictChildren = entries.Select(e => (Node: nodesByKey[e.Key], Subject: e.Value)).ToList();
        await LoadManySubjectsAsync(dictChildren, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadManySubjectsAsync(
        IReadOnlyList<(ReferenceDescription Node, IInterceptorSubject Subject)> subjects,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        CancellationToken cancellationToken)
    {
        if (subjects.Count == 0)
        {
            return;
        }

        // Step 1: Pre-browse all subjects' children in one batch
        var subjectNodeIds = new List<NodeId>(subjects.Count);
        foreach (var (node, _) in subjects)
        {
            var resolved = ExpandedNodeId.ToNodeId(node.NodeId, session.NamespaceUris);
            if (resolved is not null)
            {
                subjectNodeIds.Add(resolved);
            }
        }
        var allBrowseResults = await BrowseManyNodesAsync(subjectNodeIds, session, cancellationToken).ConfigureAwait(false);

        // Step 2: Run Phase 2 (partition) for each subject, collecting dynamic nodes across ALL subjects
        var allDynamicObjectNodeIds = new List<NodeId>();
        var allDynamicVariableNodes = new List<(NodeId NodeId, ReferenceDescription Reference)>();

        // Per-subject state needed for Phase 4
        var subjectStates = new List<SubjectLoadState>(subjects.Count);

        foreach (var (node, subject) in subjects)
        {
            if (!loadedSubjects.Add(subject))
            {
                continue;
            }

            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject is null)
            {
                continue;
            }

            var subjectNodeId = ExpandedNodeId.ToNodeId(node.NodeId, session.NamespaceUris);
            var browseResults = subjectNodeId is not null && allBrowseResults.TryGetValue(subjectNodeId, out var r)
                ? r
                : new ReferenceDescriptionCollection();
            var distinctReferences = DistinctByResolvedNodeId(browseResults, session);

            var childEntries = new List<(ReferenceDescription Reference, NodeId ResolvedNodeId, RegisteredSubjectProperty? Property, bool NeedsDynamicType)>(distinctReferences.Count);

            for (var i = 0; i < distinctReferences.Count; i++)
            {
                var (nodeReference, resolvedNodeId) = distinctReferences[i];

                var property = await FindSubjectPropertyAsync(registeredSubject, nodeReference, session, cancellationToken).ConfigureAwait(false);
                if (property is not null)
                {
                    childEntries.Add((nodeReference, resolvedNodeId, property, false));
                    continue;
                }

                var dynamicPropertyName = nodeReference.BrowseName.Name;

                var propertyExists = false;
                foreach (var childProperty in registeredSubject.Properties)
                {
                    if (childProperty.Name == dynamicPropertyName)
                    {
                        propertyExists = true;
                        break;
                    }
                }

                if (propertyExists)
                {
                    childEntries.Add((nodeReference, resolvedNodeId, null, false));
                    continue;
                }

                var addAsDynamic = _configuration.ShouldAddDynamicProperty is not null &&
                    await _configuration.ShouldAddDynamicProperty(nodeReference, cancellationToken).ConfigureAwait(false);

                if (!addAsDynamic)
                {
                    childEntries.Add((nodeReference, resolvedNodeId, null, false));
                    continue;
                }

                childEntries.Add((nodeReference, resolvedNodeId, null, true));
                if (nodeReference.NodeClass == NodeClass.Object)
                {
                    allDynamicObjectNodeIds.Add(resolvedNodeId);
                }
                else if (nodeReference.NodeClass == NodeClass.Variable)
                {
                    allDynamicVariableNodes.Add((resolvedNodeId, nodeReference));
                }
            }

            subjectStates.Add(new SubjectLoadState(subject, node, registeredSubject, childEntries));
        }

        // Step 3: Batch type resolution across ALL subjects
        var objectTypeMap = new Dictionary<NodeId, Type>();
        if (allDynamicObjectNodeIds.Count > 0)
        {
            var objectBrowseResults = await BrowseManyNodesAsync(allDynamicObjectNodeIds, session, cancellationToken).ConfigureAwait(false);
            foreach (var nodeId in allDynamicObjectNodeIds)
            {
                var children = objectBrowseResults.TryGetValue(nodeId, out var c)
                    ? c
                    : new ReferenceDescriptionCollection();
                objectTypeMap[nodeId] = OpcUaTypeResolver.ClassifyObjectNode(children);
            }
        }

        var variableTypeMap = new Dictionary<NodeId, Type?>();
        if (allDynamicVariableNodes.Count > 0)
        {
            variableTypeMap = await _configuration.TypeResolver.ResolveVariableTypesAsync(session, allDynamicVariableNodes, cancellationToken).ConfigureAwait(false);
        }

        // Step 4: Process children for all subjects, collecting attribute nodes and subject refs across all
        var allAttributeVariableNodes = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var allPendingSubjectRefs = new List<(RegisteredSubjectProperty Property, ReferenceDescription NodeReference, NodeId ResolvedNodeId, IInterceptorSubject SubjectToLoad, bool IsNew)>();

        foreach (var state in subjectStates)
        {
            for (var i = 0; i < state.ChildEntries.Count; i++)
            {
                var (nodeReference, resolvedNodeId, property, needsDynamicType) = state.ChildEntries[i];

                if (needsDynamicType && property is null)
                {
                    Type? inferredType = null;
                    if (nodeReference.NodeClass == NodeClass.Object)
                    {
                        objectTypeMap.TryGetValue(resolvedNodeId, out inferredType);
                    }
                    else if (nodeReference.NodeClass == NodeClass.Variable)
                    {
                        variableTypeMap.TryGetValue(resolvedNodeId, out inferredType);
                    }

                    if (inferredType is null)
                    {
                        _logger.LogWarning(
                            "Could not infer type for dynamic property '{PropertyName}' (NodeId: {NodeId}). Skipping property.",
                            nodeReference.BrowseName.Name, nodeReference.NodeId);
                        continue;
                    }

                    object? value = null;
                    property = state.RegisteredSubject.AddProperty(
                        nodeReference.BrowseName.Name,
                        inferredType,
                        _ => value,
                        (_, o) => value = o,
                        _configuration.TypeResolver.GetDynamicPropertyAttributes(nodeReference, session));
                }

                if (property is null)
                {
                    continue;
                }

                var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
                if (propertyName is not null)
                {
                    if (property.IsSubjectReference)
                    {
                        var nodeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(property);
                        if (nodeConfiguration?.NodeClass == Mapping.OpcUaNodeClass.Variable)
                        {
                            await LoadVariableNodeForSubjectAsync(property, resolvedNodeId, session, monitoredItems, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            if (subjectsByNodeId.TryGetValue(resolvedNodeId, out var reusedSubject))
                            {
                                property.SetValueFromSource(_source, null, null, reusedSubject);
                            }
                            else
                            {
                                var existingChildren = property.Children;
                                var existingChild = existingChildren.IsEmpty ? default : existingChildren[0];
                                var isNewSubject = existingChild.Subject is null;
                                var subjectToLoad = existingChild.Subject
                                    ?? await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeReference, session, cancellationToken).ConfigureAwait(false);

                                if (isNewSubject)
                                {
                                    subjectToLoad.Context.AddFallbackContext(state.Subject.Context);
                                }

                                subjectsByNodeId.TryAdd(resolvedNodeId, subjectToLoad);
                                allPendingSubjectRefs.Add((property, nodeReference, resolvedNodeId, subjectToLoad, isNewSubject));
                            }
                        }
                    }
                    else if (property.IsSubjectCollection)
                    {
                        await LoadSubjectCollectionAsync(property, resolvedNodeId, monitoredItems, session, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
                    }
                    else if (property.IsSubjectDictionary)
                    {
                        await LoadSubjectDictionaryAsync(property, resolvedNodeId, monitoredItems, session, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        MonitorValueNode(resolvedNodeId, property, monitoredItems);
                        allAttributeVariableNodes.Add((property, resolvedNodeId));
                    }
                }
            }
        }

        // Step 4b: Batch-load all collected subject references across all subjects
        if (allPendingSubjectRefs.Count > 0)
        {
            var refsToLoad = allPendingSubjectRefs
                .Select(r => (r.NodeReference, r.SubjectToLoad))
                .ToList();
            await LoadManySubjectsAsync(refsToLoad, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);

            foreach (var (property, _, _, subjectToLoad, isNew) in allPendingSubjectRefs)
            {
                if (isNew)
                {
                    property.SetValueFromSource(_source, null, null, subjectToLoad);
                }
            }
        }

        // Step 5: Batch attribute browsing across ALL subjects
        await LoadAttributeNodesForManyAsync(allAttributeVariableNodes, session, monitoredItems, cancellationToken).ConfigureAwait(false);
    }

    private record SubjectLoadState(
        IInterceptorSubject Subject,
        ReferenceDescription Node,
        RegisteredSubject RegisteredSubject,
        List<(ReferenceDescription Reference, NodeId ResolvedNodeId, RegisteredSubjectProperty? Property, bool NeedsDynamicType)> ChildEntries);

    private async Task<RegisteredSubjectProperty?> FindSubjectPropertyAsync(
        RegisteredSubject registeredSubject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        // Use NodeMapper for property lookup (supports attributes, path provider, and fluent config)
        return await _configuration.NodeMapper.TryGetPropertyAsync(
            registeredSubject, nodeReference, session, cancellationToken).ConfigureAwait(false);
    }

    private void MonitorValueNode(NodeId nodeId, RegisteredSubjectProperty property, List<MonitoredItem> monitoredItems)
    {
        var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property);
        property.Reference.SetPropertyData(_source.OpcUaNodeIdKey, nodeId);

        if (!_ownership.ClaimSource(property.Reference))
        {
            _logger.LogError(
                "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                property.Subject.GetType().Name, property.Name);
            return;
        }

        monitoredItems.Add(monitoredItem);
    }

    private async Task LoadVariableNodeForSubjectAsync(
        RegisteredSubjectProperty property,
        NodeId nodeId,
        ISession session,
        List<MonitoredItem> monitoredItems,
        CancellationToken cancellationToken)
    {
        var children = property.Children;
        var childSubject = (children.IsEmpty ? default : children[0]).Subject?.TryGetRegisteredSubject();
        if (childSubject == null)
        {
            return;
        }

        // Find [OpcUaValue] property and monitor the node for it
        var valueProperty = childSubject.TryGetValueProperty(_configuration.NodeMapper);
        if (valueProperty != null)
        {
            MonitorValueNode(nodeId, valueProperty, monitoredItems);
        }

        // Also load HasProperty children as regular variable nodes
        var rawChildNodes = await BrowseNodeAsync(nodeId, session, cancellationToken).ConfigureAwait(false);
        var childNodes = DistinctByResolvedNodeId(rawChildNodes, session);
        foreach (var (childNode, childNodeId) in childNodes)
        {
            // Find matching property in child subject (excluding the value property)
            foreach (var childProperty in childSubject.Properties)
            {
                if (childProperty == valueProperty)
                {
                    continue; // Skip the value property
                }

                var childPropertyName = childProperty.ResolvePropertyName(_configuration.NodeMapper);
                if (childPropertyName == childNode.BrowseName.Name)
                {
                    MonitorValueNode(childNodeId, childProperty, monitoredItems);
                    break;
                }
            }
        }
    }

    // Dedup duplicate browse entries (e.g. same target via HasComponent + HasProperty).
    // Resolves each ExpandedNodeId via the session's NamespaceTable to produce a canonical
    // NodeId for the dedup key: ExpandedNodeId compares unequal when the same target is
    // expressed with NamespaceIndex vs NamespaceUri. References with an unresolvable
    // namespace URI (ToNodeId returns null) are skipped, since they cannot be addressed
    // for monitoring or further browsing.
    private List<(ReferenceDescription Reference, NodeId NodeId)> DistinctByResolvedNodeId(
        IReadOnlyCollection<ReferenceDescription> references,
        ISession session)
    {
        var seen = new HashSet<NodeId>(references.Count);
        var result = new List<(ReferenceDescription, NodeId)>(references.Count);
        foreach (var reference in references)
        {
            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (nodeId is null)
            {
                _logger.LogWarning(
                    "Skipping browse reference '{BrowseName}' with unresolvable NodeId '{NodeId}': namespace URI is not registered in the session's NamespaceTable.",
                    reference.BrowseName?.Name, reference.NodeId);
                continue;
            }
            if (!seen.Add(nodeId))
            {
                continue;
            }
            result.Add((reference, nodeId));
        }
        return result;
    }

    private async Task<Dictionary<NodeId, ReferenceDescriptionCollection>> BrowseManyNodesAsync(
        IReadOnlyList<NodeId> nodeIds,
        ISession session,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new Dictionary<NodeId, ReferenceDescriptionCollection>(nodeIds.Count);
        if (nodeIds.Count == 0)
        {
            return result;
        }

        foreach (var nodeId in nodeIds)
        {
            result[nodeId] = new ReferenceDescriptionCollection();
        }

        var batchSize = (int)(session.OperationLimits?.MaxNodesPerBrowse ?? 0);
        if (batchSize <= 0) batchSize = 512;

        for (var offset = 0; offset < nodeIds.Count; offset += batchSize)
        {
            var end = Math.Min(offset + batchSize, nodeIds.Count);
            var browseDescriptions = new BrowseDescriptionCollection(end - offset);
            for (var i = offset; i < end; i++)
            {
                browseDescriptions.Add(new BrowseDescription
                {
                    NodeId = nodeIds[i],
                    BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                    IncludeSubtypes = true,
                    NodeClassMask = NodeClassMask,
                    ResultMask = (uint)BrowseResultMask.All
                });
            }

            var response = await session.BrowseAsync(
                null, null,
                _configuration.MaximumReferencesPerNode,
                browseDescriptions,
                cancellationToken).ConfigureAwait(false);

            // Collect results and track continuation points
            var continuationPoints = new List<(int ChunkIndex, NodeId NodeId, byte[] ContinuationPoint)>();
            for (var i = 0; i < response.Results.Count; i++)
            {
                var browseResult = response.Results[i];
                var nodeId = nodeIds[offset + i];
                if (StatusCode.IsGood(browseResult.StatusCode) && browseResult.References is { Count: > 0 })
                {
                    result[nodeId].AddRange(browseResult.References);
                }
                if (browseResult.ContinuationPoint is { Length: > 0 })
                {
                    continuationPoints.Add((i, nodeId, browseResult.ContinuationPoint));
                }
            }

            // Handle continuation points
            while (continuationPoints.Count > 0)
            {
                var cpCollection = new ByteStringCollection(continuationPoints.Count);
                foreach (var (_, _, cp) in continuationPoints)
                {
                    cpCollection.Add(cp);
                }

                var nextResponse = await session.BrowseNextAsync(
                    null, false, cpCollection, cancellationToken).ConfigureAwait(false);

                var newContinuationPoints = new List<(int ChunkIndex, NodeId NodeId, byte[] ContinuationPoint)>();
                for (var i = 0; i < nextResponse.Results.Count; i++)
                {
                    var browseResult = nextResponse.Results[i];
                    var nodeId = continuationPoints[i].NodeId;
                    if (StatusCode.IsGood(browseResult.StatusCode) && browseResult.References is { Count: > 0 })
                    {
                        result[nodeId].AddRange(browseResult.References);
                    }
                    if (browseResult.ContinuationPoint is { Length: > 0 })
                    {
                        newContinuationPoints.Add((continuationPoints[i].ChunkIndex, nodeId, browseResult.ContinuationPoint));
                    }
                }

                continuationPoints = newContinuationPoints;
            }
        }

        var totalRefs = 0;
        foreach (var refs in result.Values) totalRefs += refs.Count;
        _logger.LogInformation("BrowseManyNodesAsync: {NodeCount} nodes, {TotalRefs} total refs in {ElapsedMs}ms",
            nodeIds.Count, totalRefs, sw.ElapsedMilliseconds);
        return result;
    }

    private async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        NodeId nodeId,
        ISession session,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var browseDescriptions = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = nodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = NodeClassMask,
                ResultMask = (uint)BrowseResultMask.All
            }
        };

        var results = new ReferenceDescriptionCollection();

        var response = await session.BrowseAsync(
            null,
            null,
            _configuration.MaximumReferencesPerNode,
            browseDescriptions,
            cancellationToken).ConfigureAwait(false);

        if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
        {
            results.AddRange(response.Results[0].References);

            var continuationPoint = response.Results[0].ContinuationPoint;
            while (continuationPoint is { Length: > 0 })
            {
                var nextResponse = await session.BrowseNextAsync(
                    null, false,
                    [continuationPoint], cancellationToken).ConfigureAwait(false);

                if (nextResponse.Results.Count > 0 && StatusCode.IsGood(nextResponse.Results[0].StatusCode))
                {
                    var r0 = nextResponse.Results[0];
                    if (r0.References is { Count: > 0 } nextReferences)
                    {
                        foreach (var reference in nextReferences)
                        {
                            results.Add(reference);
                        }
                    }
                    continuationPoint = r0.ContinuationPoint;
                }
                else
                {
                    break;
                }
            }
        }

        _logger.LogInformation("BrowseNodeAsync({NodeId}): {Count} refs in {ElapsedMs}ms", nodeId, results.Count, sw.ElapsedMilliseconds);
        return results;
    }
}

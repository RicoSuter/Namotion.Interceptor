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

    private sealed class LoadContext(
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        CancellationToken cancellationToken)
    {
        public ISession Session { get; } = session;
        public List<MonitoredItem> MonitoredItems { get; } = monitoredItems;
        public HashSet<IInterceptorSubject> LoadedSubjects { get; } = loadedSubjects;
        public Dictionary<NodeId, IInterceptorSubject> SubjectsByNodeId { get; } = subjectsByNodeId;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Dictionary<NodeId, ReferenceDescriptionCollection> BrowseCache { get; } = new();
    }

    public async Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(
        IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        CancellationToken cancellationToken)
    {
        var context = new LoadContext(
            session,
            new List<MonitoredItem>(),
            new HashSet<IInterceptorSubject>(),
            new Dictionary<NodeId, IInterceptorSubject>(),
            cancellationToken);

        await LoadManySubjectsAsync([(node, subject)], context).ConfigureAwait(false);
        return context.MonitoredItems;
    }

    private async Task LoadAttributeNodesForManyAsync(
        IReadOnlyList<(RegisteredSubjectProperty Property, NodeId NodeId)> variableNodes,
        LoadContext context)
    {
        if (variableNodes.Count == 0)
        {
            return;
        }

        var visitedNodes = new HashSet<NodeId>();
        var currentRound = new List<(RegisteredSubjectProperty Property, NodeId ParentNodeId)>(variableNodes);

        while (currentRound.Count > 0)
        {
            var (nodesToBrowse, browseableEntries) = FilterUnvisitedNodes(currentRound, visitedNodes);
            if (nodesToBrowse.Count == 0)
            {
                break;
            }

            var browseResults = await BrowseManyNodesAsync(nodesToBrowse, context.Session, context.CancellationToken).ConfigureAwait(false);
            var dynamicAttributeNodes = new List<(RegisteredSubjectProperty OwnerProperty, NodeId ChildNodeId, ReferenceDescription ChildNode, string BrowseName)>();
            var nextRound = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();

            foreach (var (property, parentNodeId) in browseableEntries)
            {
                if (!browseResults.TryGetValue(parentNodeId, out var rawChildren) || rawChildren.Count == 0)
                {
                    continue;
                }

                var childNodes = DistinctByResolvedNodeId(rawChildren, context.Session);
                var processedBrowseNames = MatchKnownAttributes(property, childNodes, nextRound, context);
                await CollectDynamicAttributesAsync(property, parentNodeId, childNodes, processedBrowseNames, dynamicAttributeNodes, context).ConfigureAwait(false);
            }

            await ResolveDynamicAttributesAsync(dynamicAttributeNodes, nextRound, context).ConfigureAwait(false);
            currentRound = nextRound;
        }
    }

    private static (List<NodeId> NodeIds, List<(RegisteredSubjectProperty Property, NodeId ParentNodeId)> Entries) FilterUnvisitedNodes(
        List<(RegisteredSubjectProperty Property, NodeId ParentNodeId)> round,
        HashSet<NodeId> visitedNodes)
    {
        var nodeIds = new List<NodeId>();
        var entries = new List<(RegisteredSubjectProperty Property, NodeId ParentNodeId)>();
        foreach (var (property, parentNodeId) in round)
        {
            if (visitedNodes.Add(parentNodeId))
            {
                nodeIds.Add(parentNodeId);
                entries.Add((property, parentNodeId));
            }
        }
        return (nodeIds, entries);
    }

    private HashSet<string> MatchKnownAttributes(
        RegisteredSubjectProperty property,
        List<(ReferenceDescription Reference, NodeId NodeId)> childNodes,
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> nextRound,
        LoadContext context)
    {
        var processedBrowseNames = new HashSet<string>();
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
            MonitorValueNode(matchingNodeId, attribute, context.MonitoredItems);
            nextRound.Add((attribute, matchingNodeId));
        }
        return processedBrowseNames;
    }

    private async Task CollectDynamicAttributesAsync(
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        List<(ReferenceDescription Reference, NodeId NodeId)> childNodes,
        HashSet<string> processedBrowseNames,
        List<(RegisteredSubjectProperty OwnerProperty, NodeId ChildNodeId, ReferenceDescription ChildNode, string BrowseName)> dynamicAttributeNodes,
        LoadContext context)
    {
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
                await _configuration.ShouldAddDynamicAttribute(childNode, context.CancellationToken).ConfigureAwait(false);
            if (!addAsDynamic)
            {
                continue;
            }

            dynamicAttributeNodes.Add((property, childNodeId, childNode, browseName));
        }
    }

    private async Task ResolveDynamicAttributesAsync(
        List<(RegisteredSubjectProperty OwnerProperty, NodeId ChildNodeId, ReferenceDescription ChildNode, string BrowseName)> dynamicAttributeNodes,
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> nextRound,
        LoadContext context)
    {
        if (dynamicAttributeNodes.Count == 0)
        {
            return;
        }

        var variableInputs = new List<(NodeId NodeId, ReferenceDescription Reference)>(dynamicAttributeNodes.Count);
        foreach (var (_, childNodeId, childNode, _) in dynamicAttributeNodes)
        {
            variableInputs.Add((childNodeId, childNode));
        }
        var resolvedTypes = await _configuration.TypeResolver.ResolveVariableTypesAsync(context.Session, variableInputs, context.CancellationToken).ConfigureAwait(false);

        foreach (var (ownerProperty, childNodeId, childNode, browseName) in dynamicAttributeNodes)
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
                _configuration.TypeResolver.GetDynamicPropertyAttributes(childNode, context.Session));

            MonitorValueNode(childNodeId, dynamicAttribute, context.MonitoredItems);
            nextRound.Add((dynamicAttribute, childNodeId));
        }
    }

    private async Task<List<(ReferenceDescription Node, IInterceptorSubject Subject)>> ResolveChildSubjectsAsync(
        RegisteredSubjectProperty property,
        List<(ReferenceDescription Reference, NodeId NodeId)> childNodes,
        bool isDictionary,
        LoadContext context)
    {
        var existingChildren = property.Children;
        var existingByKey = isDictionary
            ? existingChildren.ToDictionary(c => c.Index!, c => c.Subject)
            : null;

        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(childNodes.Count);

        for (var i = 0; i < childNodes.Count; i++)
        {
            var (childNode, nodeId) = childNodes[i];

            IInterceptorSubject? childSubject;
            object factoryIndex;

            if (isDictionary)
            {
                var key = childNode.BrowseName.Name;
                childSubject = existingByKey!.GetValueOrDefault(key);
                factoryIndex = key;
            }
            else
            {
                childSubject = i < existingChildren.Length ? existingChildren[i].Subject : null;
                factoryIndex = i;
            }

            if (childSubject is null)
            {
                context.SubjectsByNodeId.TryGetValue(nodeId, out childSubject);
            }

            childSubject ??= await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                property, childNode, factoryIndex, context.Session, context.CancellationToken).ConfigureAwait(false);

            context.SubjectsByNodeId.TryAdd(nodeId, childSubject);
            children.Add((childNode, childSubject));
        }

        return children;
    }

    private async Task LoadManySubjectsAsync(
        IReadOnlyList<(ReferenceDescription Node, IInterceptorSubject Subject)> subjects,
        LoadContext context)
    {
        if (subjects.Count == 0)
        {
            return;
        }

        // Step 1: Filter valid subjects, batch-browse
        var (validSubjects, allBrowseResults) = await FilterAndBrowseSubjectsAsync(subjects, context).ConfigureAwait(false);
        if (validSubjects.Count == 0)
        {
            return;
        }

        // Step 2: Classify children, collect dynamic nodes
        var allDynamicObjectNodeIds = new List<NodeId>();
        var allDynamicVariableNodes = new List<(NodeId NodeId, ReferenceDescription Reference)>();
        var subjectStates = new List<SubjectLoadState>(validSubjects.Count);

        foreach (var (node, subject, registeredSubject, subjectNodeId) in validSubjects)
        {
            var browseResults = subjectNodeId is not null && allBrowseResults.TryGetValue(subjectNodeId, out var r)
                ? r
                : new ReferenceDescriptionCollection();
            var distinctReferences = DistinctByResolvedNodeId(browseResults, context.Session);

            var childEntries = await ClassifyChildReferencesAsync(
                registeredSubject, distinctReferences,
                allDynamicObjectNodeIds, allDynamicVariableNodes,
                context.Session, context.CancellationToken).ConfigureAwait(false);

            subjectStates.Add(new SubjectLoadState(subject, node, registeredSubject, childEntries));
        }

        // Step 3: Batch resolve types (populates BrowseCache for reuse by Collections and next-level Subjects)
        var objectTypeMap = await ResolveObjectTypesAsync(allDynamicObjectNodeIds, context).ConfigureAwait(false);

        var variableTypeMap = allDynamicVariableNodes.Count > 0
            ? await _configuration.TypeResolver.ResolveVariableTypesAsync(context.Session, allDynamicVariableNodes, context.CancellationToken).ConfigureAwait(false)
            : new Dictionary<NodeId, Type?>();

        // Step 4: Classify children into batches
        var allAttributeVariableNodes = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var allPendingSubjectRefs = new List<(RegisteredSubjectProperty Property, ReferenceDescription NodeReference, NodeId ResolvedNodeId, IInterceptorSubject SubjectToLoad, bool IsNew)>();
        var pendingVariableNodes = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var pendingCollections = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var pendingDictionaries = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();

        foreach (var state in subjectStates)
        {
            for (var i = 0; i < state.ChildEntries.Count; i++)
            {
                var (nodeReference, resolvedNodeId, property, needsDynamicType) = state.ChildEntries[i];

                if (needsDynamicType && property is null)
                {
                    property = TryCreateDynamicProperty(
                        state.RegisteredSubject, nodeReference, resolvedNodeId,
                        objectTypeMap, variableTypeMap, context.Session);
                }

                if (property is null)
                {
                    continue;
                }

                var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
                if (propertyName is null)
                {
                    continue;
                }

                if (property.IsSubjectReference)
                {
                    var nodeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(property);
                    if (nodeConfiguration?.NodeClass == Mapping.OpcUaNodeClass.Variable)
                    {
                        pendingVariableNodes.Add((property, resolvedNodeId));
                    }
                    else
                    {
                        var result = await PrepareSubjectReferenceAsync(
                            property, nodeReference, resolvedNodeId, state.Subject,
                            context).ConfigureAwait(false);

                        if (result is not null)
                        {
                            var (subjectToLoad, isNew) = result.Value;
                            allPendingSubjectRefs.Add((property, nodeReference, resolvedNodeId, subjectToLoad, isNew));
                        }
                    }
                }
                else if (property.IsSubjectCollection)
                {
                    pendingCollections.Add((property, resolvedNodeId));
                }
                else if (property.IsSubjectDictionary)
                {
                    pendingDictionaries.Add((property, resolvedNodeId));
                }
                else
                {
                    MonitorValueNode(resolvedNodeId, property, context.MonitoredItems);
                    allAttributeVariableNodes.Add((property, resolvedNodeId));
                }
            }
        }

        // Step 4b: Batch-load variable nodes
        await BatchLoadVariableNodesAsync(pendingVariableNodes, context).ConfigureAwait(false);

        // Step 4c: Batch-load collections and dictionaries
        await BatchLoadCollectionsAndDictionariesAsync(pendingCollections, pendingDictionaries, context).ConfigureAwait(false);

        // Step 4d: Batch-load pending subject references
        await LoadPendingSubjectReferencesAsync(allPendingSubjectRefs, context).ConfigureAwait(false);

        // Step 5: Batch attribute browsing
        await LoadAttributeNodesForManyAsync(allAttributeVariableNodes, context).ConfigureAwait(false);
    }

    private async Task<(
        List<(ReferenceDescription Node, IInterceptorSubject Subject, RegisteredSubject RegisteredSubject, NodeId? NodeId)> ValidSubjects,
        Dictionary<NodeId, ReferenceDescriptionCollection> BrowseResults)>
        FilterAndBrowseSubjectsAsync(
            IReadOnlyList<(ReferenceDescription Node, IInterceptorSubject Subject)> subjects,
            LoadContext context)
    {
        var validSubjects = new List<(ReferenceDescription Node, IInterceptorSubject Subject, RegisteredSubject RegisteredSubject, NodeId? NodeId)>(subjects.Count);
        var subjectNodeIds = new List<NodeId>(subjects.Count);

        foreach (var (node, subject) in subjects)
        {
            if (!context.LoadedSubjects.Add(subject))
            {
                continue;
            }

            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject is null)
            {
                continue;
            }

            var resolved = ExpandedNodeId.ToNodeId(node.NodeId, context.Session.NamespaceUris);
            validSubjects.Add((node, subject, registeredSubject, resolved));
            if (resolved is not null)
            {
                subjectNodeIds.Add(resolved);
            }
        }

        var uncachedNodeIds = new List<NodeId>(subjectNodeIds.Count);
        var browseResults = new Dictionary<NodeId, ReferenceDescriptionCollection>(subjectNodeIds.Count);
        foreach (var nodeId in subjectNodeIds)
        {
            if (context.BrowseCache.TryGetValue(nodeId, out var cached))
            {
                browseResults[nodeId] = cached;
            }
            else
            {
                uncachedNodeIds.Add(nodeId);
            }
        }

        if (uncachedNodeIds.Count > 0)
        {
            var freshResults = await BrowseManyNodesAsync(uncachedNodeIds, context.Session, context.CancellationToken).ConfigureAwait(false);
            foreach (var (nodeId, refs) in freshResults)
            {
                browseResults[nodeId] = refs;
            }
        }

        return (validSubjects, browseResults);
    }

    private async Task<List<(ReferenceDescription Reference, NodeId ResolvedNodeId, RegisteredSubjectProperty? Property, bool NeedsDynamicType)>>
        ClassifyChildReferencesAsync(
            RegisteredSubject registeredSubject,
            List<(ReferenceDescription Reference, NodeId NodeId)> distinctReferences,
            List<NodeId> dynamicObjectNodeIds,
            List<(NodeId NodeId, ReferenceDescription Reference)> dynamicVariableNodes,
            ISession session,
            CancellationToken cancellationToken)
    {
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

            if (registeredSubject.TryGetProperty(dynamicPropertyName) is not null)
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
                dynamicObjectNodeIds.Add(resolvedNodeId);
            }
            else if (nodeReference.NodeClass == NodeClass.Variable)
            {
                dynamicVariableNodes.Add((resolvedNodeId, nodeReference));
            }
        }

        return childEntries;
    }

    private async Task<Dictionary<NodeId, Type>> ResolveObjectTypesAsync(
        IReadOnlyList<NodeId> objectNodeIds,
        LoadContext context)
    {
        var objectTypeMap = new Dictionary<NodeId, Type>();
        if (objectNodeIds.Count == 0)
        {
            return objectTypeMap;
        }

        var objectBrowseResults = await BrowseManyNodesAsync(objectNodeIds, context.Session, context.CancellationToken).ConfigureAwait(false);
        foreach (var nodeId in objectNodeIds)
        {
            var children = objectBrowseResults.TryGetValue(nodeId, out var c)
                ? c
                : new ReferenceDescriptionCollection();
            objectTypeMap[nodeId] = OpcUaTypeResolver.ClassifyObjectNode(children);
            context.BrowseCache[nodeId] = children;
        }

        return objectTypeMap;
    }

    private RegisteredSubjectProperty? TryCreateDynamicProperty(
        RegisteredSubject registeredSubject,
        ReferenceDescription nodeReference,
        NodeId resolvedNodeId,
        Dictionary<NodeId, Type> objectTypeMap,
        Dictionary<NodeId, Type?> variableTypeMap,
        ISession session)
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
            return null;
        }

        object? value = null;
        return registeredSubject.AddProperty(
            nodeReference.BrowseName.Name,
            inferredType,
            _ => value,
            (_, o) => value = o,
            _configuration.TypeResolver.GetDynamicPropertyAttributes(nodeReference, session));
    }

    private async Task<(IInterceptorSubject Subject, bool IsNew)?> PrepareSubjectReferenceAsync(
        RegisteredSubjectProperty property,
        ReferenceDescription nodeReference,
        NodeId resolvedNodeId,
        IInterceptorSubject parentSubject,
        LoadContext context)
    {
        if (context.SubjectsByNodeId.TryGetValue(resolvedNodeId, out var reusedSubject))
        {
            property.SetValueFromSource(_source, null, null, reusedSubject);
            return null;
        }

        var existingChildren = property.Children;
        var existingChild = existingChildren.IsEmpty ? default : existingChildren[0];
        var isNewSubject = existingChild.Subject is null;
        var subjectToLoad = existingChild.Subject
            ?? await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeReference, context.Session, context.CancellationToken).ConfigureAwait(false);

        if (isNewSubject)
        {
            subjectToLoad.Context.AddFallbackContext(parentSubject.Context);
        }

        context.SubjectsByNodeId.TryAdd(resolvedNodeId, subjectToLoad);
        return (subjectToLoad, isNewSubject);
    }

    private async Task LoadPendingSubjectReferencesAsync(
        List<(RegisteredSubjectProperty Property, ReferenceDescription NodeReference, NodeId ResolvedNodeId, IInterceptorSubject SubjectToLoad, bool IsNew)> pendingRefs,
        LoadContext context)
    {
        if (pendingRefs.Count == 0)
        {
            return;
        }

        var refsToLoad = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(pendingRefs.Count);
        foreach (var (_, nodeReference, _, subjectToLoad, _) in pendingRefs)
        {
            refsToLoad.Add((nodeReference, subjectToLoad));
        }
        await LoadManySubjectsAsync(refsToLoad, context).ConfigureAwait(false);

        foreach (var (property, _, _, subjectToLoad, isNew) in pendingRefs)
        {
            if (isNew)
            {
                property.SetValueFromSource(_source, null, null, subjectToLoad);
            }
        }
    }

    private async Task BatchLoadVariableNodesAsync(
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> variableNodes,
        LoadContext context)
    {
        if (variableNodes.Count == 0)
        {
            return;
        }

        var nodesToBrowse = new List<(NodeId NodeId, RegisteredSubject ChildSubject, RegisteredSubjectProperty? ValueProperty)>();
        var nodeIds = new List<NodeId>(variableNodes.Count);

        foreach (var (property, nodeId) in variableNodes)
        {
            var children = property.Children;
            var childSubject = (children.IsEmpty ? default : children[0]).Subject?.TryGetRegisteredSubject();
            if (childSubject is null)
            {
                continue;
            }

            var valueProperty = childSubject.TryGetValueProperty(_configuration.NodeMapper);
            if (valueProperty is not null)
            {
                MonitorValueNode(nodeId, valueProperty, context.MonitoredItems);
            }

            nodesToBrowse.Add((nodeId, childSubject, valueProperty));
            nodeIds.Add(nodeId);
        }

        if (nodeIds.Count == 0)
        {
            return;
        }

        var browseResults = await BrowseManyNodesAsync(nodeIds, context.Session, context.CancellationToken).ConfigureAwait(false);

        foreach (var (nodeId, childSubject, valueProperty) in nodesToBrowse)
        {
            if (!browseResults.TryGetValue(nodeId, out var rawChildren))
            {
                continue;
            }

            var childNodes = DistinctByResolvedNodeId(rawChildren, context.Session);
            foreach (var (childNode, childNodeId) in childNodes)
            {
                foreach (var childProperty in childSubject.Properties)
                {
                    if (childProperty == valueProperty)
                    {
                        continue;
                    }

                    var childPropertyName = childProperty.ResolvePropertyName(_configuration.NodeMapper);
                    if (childPropertyName == childNode.BrowseName.Name)
                    {
                        MonitorValueNode(childNodeId, childProperty, context.MonitoredItems);
                        break;
                    }
                }
            }
        }
    }

    private async Task BatchLoadCollectionsAndDictionariesAsync(
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> pendingCollections,
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> pendingDictionaries,
        LoadContext context)
    {
        var totalCount = pendingCollections.Count + pendingDictionaries.Count;
        if (totalCount == 0)
        {
            return;
        }

        var missingNodeIds = new HashSet<NodeId>();
        foreach (var (_, nodeId) in pendingCollections)
        {
            if (!context.BrowseCache.ContainsKey(nodeId))
            {
                missingNodeIds.Add(nodeId);
            }
        }
        foreach (var (_, nodeId) in pendingDictionaries)
        {
            if (!context.BrowseCache.ContainsKey(nodeId))
            {
                missingNodeIds.Add(nodeId);
            }
        }

        if (missingNodeIds.Count > 0)
        {
            var freshResults = await BrowseManyNodesAsync(missingNodeIds.ToList(), context.Session, context.CancellationToken).ConfigureAwait(false);
            foreach (var (nodeId, refs) in freshResults)
            {
                context.BrowseCache[nodeId] = refs;
            }
        }

        var browseResults = context.BrowseCache;
        var allChildrenToLoad = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>();

        foreach (var (property, nodeId) in pendingCollections)
        {
            var rawChildren = browseResults.TryGetValue(nodeId, out var r) ? r : new ReferenceDescriptionCollection();
            var childNodes = DistinctByResolvedNodeId(rawChildren, context.Session);

            var children = await ResolveChildSubjectsAsync(property, childNodes, isDictionary: false, context).ConfigureAwait(false);

            var collection = DefaultSubjectFactory.Instance.CreateSubjectCollection(property.Type, children.Select(c => c.Subject));
            property.SetValueFromSource(_source, null, null, collection);
            allChildrenToLoad.AddRange(children);
        }

        foreach (var (property, nodeId) in pendingDictionaries)
        {
            var rawChildren = browseResults.TryGetValue(nodeId, out var r) ? r : new ReferenceDescriptionCollection();
            var childNodes = DistinctByResolvedNodeId(rawChildren, context.Session);

            var children = await ResolveChildSubjectsAsync(property, childNodes, isDictionary: true, context).ConfigureAwait(false);

            var entries = new Dictionary<object, IInterceptorSubject>(children.Count);
            foreach (var (node, subject) in children)
            {
                entries[node.BrowseName.Name] = subject;
            }

            var dictionary = DefaultSubjectFactory.Instance.CreateSubjectDictionary(property.Type, entries);
            property.SetValueFromSource(_source, null, null, dictionary);
            allChildrenToLoad.AddRange(children);
        }

        await LoadManySubjectsAsync(allChildrenToLoad, context).ConfigureAwait(false);
    }

    private record SubjectLoadState(
        IInterceptorSubject Subject,
        ReferenceDescription Node,
        RegisteredSubject RegisteredSubject,
        List<(ReferenceDescription Reference, NodeId ResolvedNodeId, RegisteredSubjectProperty? Property, bool NeedsDynamicType)> ChildEntries);

    private Task<RegisteredSubjectProperty?> FindSubjectPropertyAsync(
        RegisteredSubject registeredSubject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken) =>
        _configuration.NodeMapper.TryGetPropertyAsync(registeredSubject, nodeReference, session, cancellationToken);

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

    // ExpandedNodeId compares unequal when the same target is expressed with NamespaceIndex
    // vs NamespaceUri; resolving to NodeId via the session's NamespaceTable produces a
    // canonical key for dedup. Unresolvable namespace URIs (ToNodeId returns null) are
    // skipped since they cannot be addressed for monitoring or further browsing.
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

            var continuationPoints = new List<(NodeId NodeId, byte[] ContinuationPoint)>();
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
                    continuationPoints.Add((nodeId, browseResult.ContinuationPoint));
                }
            }

            while (continuationPoints.Count > 0)
            {
                var cpCollection = new ByteStringCollection(continuationPoints.Count);
                foreach (var (_, cp) in continuationPoints)
                {
                    cpCollection.Add(cp);
                }

                var nextResponse = await session.BrowseNextAsync(
                    null, false, cpCollection, cancellationToken).ConfigureAwait(false);

                var newContinuationPoints = new List<(NodeId NodeId, byte[] ContinuationPoint)>();
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
                        newContinuationPoints.Add((nodeId, browseResult.ContinuationPoint));
                    }
                }

                continuationPoints = newContinuationPoints;
            }
        }

        return result;
    }
}

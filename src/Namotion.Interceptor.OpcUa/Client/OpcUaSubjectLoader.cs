using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectLoader
{
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
        public Dictionary<NodeId, IReadOnlyList<ReferenceDescription>> BrowseCache { get; } = new();
        public CancellationToken CancellationToken { get; } = cancellationToken;
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

        await LoadSubjectsAsync([(node, subject)], context).ConfigureAwait(false);
        return context.MonitoredItems;
    }

    private async Task LoadSubjectsAsync(
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
        var allDynamicObjectNodeIds = new HashSet<NodeId>();
        var allDynamicVariableNodes = new List<ReferenceDescription>();
        var subjectStates = new List<(IInterceptorSubject Subject, RegisteredSubject RegisteredSubject, List<(ReferenceDescription Reference, NodeId ResolvedNodeId, RegisteredSubjectProperty? Property, bool NeedsDynamicType)> ChildEntries)>(validSubjects.Count);

        foreach (var (subject, registeredSubject, subjectNodeId) in validSubjects)
        {
            var browseResults = subjectNodeId is not null && 
                allBrowseResults.TryGetValue(subjectNodeId, out var collection) ? collection : [];

            var distinctReferences = context.Session.DistinctByResolvedNodeId(browseResults, _logger);

            var childEntries = await ClassifyChildReferencesAsync(
                registeredSubject, distinctReferences,
                allDynamicObjectNodeIds, allDynamicVariableNodes,
                context.Session, context.CancellationToken).ConfigureAwait(false);

            subjectStates.Add((subject, registeredSubject, childEntries));
        }

        // Step 3: Batch resolve types (populates BrowseCache for reuse by Collections and next-level Subjects)
        var objectTypeMap = await ResolveObjectTypesAsync(allDynamicObjectNodeIds, context).ConfigureAwait(false);

        var variableTypeMap = allDynamicVariableNodes.Count > 0
            ? await _configuration.TypeResolver.ResolveVariableTypesAsync(context.Session, allDynamicVariableNodes, context.CancellationToken).ConfigureAwait(false)
            : new Dictionary<NodeId, Type?>();

        // Step 4: Dispatch properties and load children
        await LoadChildPropertiesAsync(subjectStates, objectTypeMap, variableTypeMap, context).ConfigureAwait(false);
    }

    private async Task<(
        List<(IInterceptorSubject Subject, RegisteredSubject RegisteredSubject, NodeId? NodeId)> ValidSubjects,
        Dictionary<NodeId, IReadOnlyList<ReferenceDescription>> BrowseResults)>
        FilterAndBrowseSubjectsAsync(
            IReadOnlyList<(ReferenceDescription Node, IInterceptorSubject Subject)> subjects,
            LoadContext context)
    {
        var validSubjects = new List<(IInterceptorSubject Subject, RegisteredSubject RegisteredSubject, NodeId? NodeId)>(subjects.Count);
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
            validSubjects.Add((subject, registeredSubject, resolved));
            if (resolved is not null)
            {
                subjectNodeIds.Add(resolved);
            }
        }

        var uncachedNodeIds = new List<NodeId>(subjectNodeIds.Count);
        var browseResults = new Dictionary<NodeId, IReadOnlyList<ReferenceDescription>>(subjectNodeIds.Count);
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
            var freshResults = await BrowseAndCacheAsync(uncachedNodeIds, context).ConfigureAwait(false);
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
            HashSet<NodeId> dynamicObjectNodeIds,
            List<ReferenceDescription> dynamicVariableNodes,
            ISession session,
            CancellationToken cancellationToken)
    {
        var childEntries = new List<(ReferenceDescription Reference, NodeId ResolvedNodeId, RegisteredSubjectProperty? Property, bool NeedsDynamicType)>(distinctReferences.Count);

        for (var i = 0; i < distinctReferences.Count; i++)
        {
            var (nodeReference, resolvedNodeId) = distinctReferences[i];

            var property = await _configuration.NodeMapper
                .TryGetPropertyAsync(registeredSubject, nodeReference, session, cancellationToken)
                .ConfigureAwait(false);

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
                dynamicVariableNodes.Add(nodeReference);
            }
        }

        return childEntries;
    }

    private async Task<Dictionary<NodeId, Type>> ResolveObjectTypesAsync(IReadOnlyCollection<NodeId> objectNodeIds, LoadContext context)
    {
        var objectTypeMap = new Dictionary<NodeId, Type>();
        if (objectNodeIds.Count == 0)
        {
            return objectTypeMap;
        }

        var objectBrowseResults = await BrowseAndCacheAsync(objectNodeIds, context).ConfigureAwait(false);
        foreach (var nodeId in objectNodeIds)
        {
            objectTypeMap[nodeId] = _configuration.TypeResolver.ResolveObjectNodeType(objectBrowseResults[nodeId]);
        }

        return objectTypeMap;
    }

    private async Task LoadChildPropertiesAsync(
        List<(IInterceptorSubject Subject, RegisteredSubject RegisteredSubject, List<(ReferenceDescription Reference, NodeId ResolvedNodeId, RegisteredSubjectProperty? Property, bool NeedsDynamicType)> ChildEntries)> subjectStates,
        Dictionary<NodeId, Type> objectTypeMap,
        IReadOnlyDictionary<NodeId, Type?> variableTypeMap,
        LoadContext context)
    {
        var allAttributeVariableNodes = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var allPendingSubjectRefs = new List<(RegisteredSubjectProperty Property, ReferenceDescription NodeReference, NodeId ResolvedNodeId, IInterceptorSubject SubjectToLoad, bool IsNew)>();
        var pendingVariableNodes = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var pendingCollections = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var pendingDictionaries = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();

        foreach (var (stateSubject, stateRegisteredSubject, stateChildEntries) in subjectStates)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < stateChildEntries.Count; i++)
            {
                var (nodeReference, resolvedNodeId, property, needsDynamicType) = stateChildEntries[i];

                if (needsDynamicType && property is null)
                {
                    property = TryCreateDynamicProperty(
                        stateRegisteredSubject, nodeReference, resolvedNodeId,
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
                            property, nodeReference, resolvedNodeId, stateSubject,
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

        await BatchLoadVariableNodesAsync(pendingVariableNodes, context).ConfigureAwait(false);
        await BatchLoadCollectionsAndDictionariesAsync(pendingCollections, pendingDictionaries, context).ConfigureAwait(false);
        await LoadPendingSubjectReferencesAsync(allPendingSubjectRefs, context).ConfigureAwait(false);
        await LoadAttributeNodesForManyAsync(allAttributeVariableNodes, context).ConfigureAwait(false);
    }

    private RegisteredSubjectProperty? TryCreateDynamicProperty(
        RegisteredSubject registeredSubject,
        ReferenceDescription nodeReference,
        NodeId resolvedNodeId,
        Dictionary<NodeId, Type> objectTypeMap,
        IReadOnlyDictionary<NodeId, Type?> variableTypeMap,
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

    private async Task BatchLoadVariableNodesAsync(
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> variableNodes,
        LoadContext context)
    {
        if (variableNodes.Count == 0)
        {
            return;
        }

        var nodesToBrowse = new List<(NodeId NodeId, RegisteredSubject ChildSubject, RegisteredSubjectProperty? ValueProperty)>();
        var nodeIds = new HashSet<NodeId>(variableNodes.Count);

        foreach (var (property, nodeId) in variableNodes)
        {
            var children = property.Children;
            var childSubject = (children.IsEmpty ? default : children[0]).Subject?.TryGetRegisteredSubject();
            if (childSubject is null)
            {
                _logger.LogWarning(
                    "Skipping OPC UA Variable-typed subject reference '{Subject}.{Property}' (NodeId: {NodeId}): no child subject was pre-constructed. The parent type must instantiate this property in its constructor.",
                    property.Subject.GetType().Name, property.Name, nodeId);
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

        var browseResults = await BrowseAndCacheAsync(nodeIds, context).ConfigureAwait(false);

        foreach (var (nodeId, childSubject, valueProperty) in nodesToBrowse)
        {
            if (!browseResults.TryGetValue(nodeId, out var rawChildren))
            {
                continue;
            }

            var childNodes = context.Session.DistinctByResolvedNodeId(rawChildren, _logger);
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
            await BrowseAndCacheAsync(missingNodeIds, context).ConfigureAwait(false);
        }

        var browseResults = context.BrowseCache;
        var allChildrenToLoad = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>();

        foreach (var (property, nodeId) in pendingCollections)
        {
            var rawChildren = browseResults.TryGetValue(nodeId, out var r) ? r : new ReferenceDescriptionCollection();
            var childNodes = context.Session.DistinctByResolvedNodeId(rawChildren, _logger);

            var children = await ResolveChildSubjectsAsync(property, childNodes, isDictionary: false, context).ConfigureAwait(false);

            var collection = DefaultSubjectFactory.Instance.CreateSubjectCollection(property.Type, children.Select(c => c.Subject));
            property.SetValueFromSource(_source, null, null, collection);
            allChildrenToLoad.AddRange(children);
        }

        foreach (var (property, nodeId) in pendingDictionaries)
        {
            var rawChildren = browseResults.TryGetValue(nodeId, out var r) ? r : new ReferenceDescriptionCollection();
            var childNodes = context.Session.DistinctByResolvedNodeId(rawChildren, _logger);

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

        await LoadSubjectsAsync(allChildrenToLoad, context).ConfigureAwait(false);
    }

    // Reuses existing children when possible: dictionaries match by browse name,
    // collections match by index position (so server-side reordering between loads
    // rebinds existing subjects to new items).
    private async Task<List<(ReferenceDescription Node, IInterceptorSubject Subject)>> ResolveChildSubjectsAsync(
        RegisteredSubjectProperty property,
        List<(ReferenceDescription Reference, NodeId NodeId)> childNodes,
        bool isDictionary,
        LoadContext context)
    {
        var existingChildren = property.Children;
        var existingByKey = isDictionary
            ? existingChildren.Where(c => c.Index is not null).ToDictionary(c => c.Index!, c => c.Subject)
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
        await LoadSubjectsAsync(refsToLoad, context).ConfigureAwait(false);

        foreach (var (property, _, _, subjectToLoad, isNew) in pendingRefs)
        {
            if (isNew)
            {
                property.SetValueFromSource(_source, null, null, subjectToLoad);
            }
        }
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
        // Tracks (property, parentNodeId) pairs already processed in any prior round, so
        // we never call MatchKnownAttributes / CollectDynamicAttributesAsync twice for the
        // same pair. With the BrowseCache fallback, a property could legitimately appear
        // in multiple rounds against different parent NodeIds, but processing the SAME
        // pair twice would re-claim already-monitored sub-attribute references.
        var processedEntries = new HashSet<(RegisteredSubjectProperty Property, NodeId ParentNodeId)>();
        var currentRound = new List<(RegisteredSubjectProperty Property, NodeId ParentNodeId)>(variableNodes);

        // Bounded to defend against pathological dynamic-attribute cycles where each
        // round mutates the registry and produces more entries; real OPC UA hierarchies
        // are shallow, so 100 levels is far beyond any realistic address space depth.
        const int maxRounds = 100;
        var round = 0;
        while (currentRound.Count > 0)
        {
            if (++round > maxRounds)
            {
                _logger.LogWarning(
                    "Aborting attribute traversal after {MaxRounds} rounds with {Remaining} entries still pending. Possible cycle in address space or attribute registration.",
                    maxRounds, currentRound.Count);
                break;
            }

            var nodesToBrowse = new List<NodeId>();
            foreach (var (_, parentNodeId) in currentRound)
            {
                if (visitedNodes.Add(parentNodeId))
                {
                    nodesToBrowse.Add(parentNodeId);
                }
            }

            var browseResults = nodesToBrowse.Count > 0
                ? await BrowseAndCacheAsync(nodesToBrowse, context).ConfigureAwait(false)
                : new Dictionary<NodeId, IReadOnlyList<ReferenceDescription>>();

            var dynamicAttributeNodes = new List<(RegisteredSubjectProperty OwnerProperty, NodeId ChildNodeId, ReferenceDescription ChildNode, string BrowseName)>();
            var nextRound = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();

            foreach (var (property, parentNodeId) in currentRound)
            {
                if (!processedEntries.Add((property, parentNodeId)))
                {
                    continue;
                }

                // Fall back to BrowseCache when a parent NodeId was browsed in an earlier
                // round (or by phase 3): siblings that share an ancestor across rounds must
                // still see the cached children, otherwise their attributes are silently lost.
                if (!browseResults.TryGetValue(parentNodeId, out var rawChildren))
                {
                    context.BrowseCache.TryGetValue(parentNodeId, out rawChildren);
                }

                if (rawChildren is null || rawChildren.Count == 0)
                {
                    continue;
                }

                var childNodes = context.Session.DistinctByResolvedNodeId(rawChildren, _logger);
                var processedBrowseNames = MatchKnownAttributes(property, childNodes, nextRound, context);
                await CollectDynamicAttributesAsync(property, parentNodeId, childNodes, processedBrowseNames, dynamicAttributeNodes, context).ConfigureAwait(false);
            }

            await ResolveDynamicAttributesAsync(dynamicAttributeNodes, nextRound, context).ConfigureAwait(false);
            currentRound = nextRound;
        }
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

        var variableReferences = new List<ReferenceDescription>(dynamicAttributeNodes.Count);
        foreach (var (_, _, childNode, _) in dynamicAttributeNodes)
        {
            variableReferences.Add(childNode);
        }
        var resolvedTypes = await _configuration.TypeResolver.ResolveVariableTypesAsync(context.Session, variableReferences, context.CancellationToken).ConfigureAwait(false);

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

    private void MonitorValueNode(NodeId nodeId, RegisteredSubjectProperty property, List<MonitoredItem> monitoredItems)
    {
        if (!_ownership.ClaimSource(property.Reference))
        {
            _logger.LogError(
                "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                property.Subject.GetType().Name, property.Name);
            return;
        }

        property.Reference.SetPropertyData(_source.OpcUaNodeIdKey, nodeId);
        var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property);
        monitoredItems.Add(monitoredItem);
    }

    // Browses the given NodeIds and writes the results into context.BrowseCache. Every
    // unique input NodeId is present as a key in both the returned view and the cache.
    private async Task<Dictionary<NodeId, IReadOnlyList<ReferenceDescription>>> BrowseAndCacheAsync(
        IReadOnlyCollection<NodeId> nodeIds,
        LoadContext context)
    {
        var results = await context.Session.BrowseNodesAsync(
            nodeIds, _configuration.MaximumReferencesPerNode, _logger, context.CancellationToken).ConfigureAwait(false);
        var view = new Dictionary<NodeId, IReadOnlyList<ReferenceDescription>>(results.Count);
        foreach (var (nodeId, refs) in results)
        {
            context.BrowseCache[nodeId] = refs;
            view[nodeId] = refs;
        }
        return view;
    }
}

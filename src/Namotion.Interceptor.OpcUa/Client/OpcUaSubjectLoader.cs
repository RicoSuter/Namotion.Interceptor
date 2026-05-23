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
    private readonly SourceOwnershipManager _ownership;
    private readonly OpcUaSubjectClientSource _source;
    private readonly ILogger _logger;

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

    private readonly record struct ChildEntry(
        ReferenceDescription Reference,
        NodeId ResolvedNodeId,
        RegisteredSubjectProperty? Property,
        bool NeedsDynamicType);

    private readonly record struct SubjectState(
        IInterceptorSubject Subject,
        RegisteredSubject Registered,
        List<ChildEntry> ChildEntries);

    private readonly record struct PendingSubjectRef(
        RegisteredSubjectProperty Property,
        ReferenceDescription NodeReference,
        IInterceptorSubject SubjectToLoad,
        bool IsNew);

    private readonly record struct DynamicAttributeNode(
        RegisteredSubjectProperty OwnerProperty,
        NodeId ChildNodeId,
        ReferenceDescription ChildNode,
        string BrowseName);

    public async Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(
        IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        CancellationToken cancellationToken)
    {
        var context = new OpcUaLoadContext(
            session,
            _configuration.MaximumReferencesPerNode,
            _configuration.MaxBrowseContinuations,
            _logger,
            cancellationToken);

        await LoadSubjectsAsync([(node, subject)], context).ConfigureAwait(false);
        return context.MonitoredItems;
    }

    private async Task LoadSubjectsAsync(
        IReadOnlyList<(ReferenceDescription Node, IInterceptorSubject Subject)> subjects,
        OpcUaLoadContext context)
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
        var subjectStates = new List<SubjectState>(validSubjects.Count);

        foreach (var (subject, registeredSubject, subjectNodeId) in validSubjects)
        {
            var browseResults = subjectNodeId is not null &&
                allBrowseResults.TryGetValue(subjectNodeId, out var collection) ? collection : [];

            var distinctReferences = context.DistinctByResolvedNodeId(browseResults);

            var childEntries = await ClassifyChildReferencesAsync(
                registeredSubject, distinctReferences,
                allDynamicObjectNodeIds, allDynamicVariableNodes,
                context.Session, context.CancellationToken).ConfigureAwait(false);

            subjectStates.Add(new SubjectState(subject, registeredSubject, childEntries));
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
            OpcUaLoadContext context)
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

            var resolved = context.ResolveNodeId(node.NodeId);
            validSubjects.Add((subject, registeredSubject, resolved));
            if (resolved is not null)
            {
                subjectNodeIds.Add(resolved);
            }
        }

        var browseResults = await context.BrowseAsync(subjectNodeIds).ConfigureAwait(false);
        return (validSubjects, browseResults);
    }

    private async Task<List<ChildEntry>>
        ClassifyChildReferencesAsync(
            RegisteredSubject registeredSubject,
            List<(ReferenceDescription Reference, NodeId NodeId)> distinctReferences,
            HashSet<NodeId> dynamicObjectNodeIds,
            List<ReferenceDescription> dynamicVariableNodes,
            ISession session,
            CancellationToken cancellationToken)
    {
        var childEntries = new List<ChildEntry>(distinctReferences.Count);

        for (var i = 0; i < distinctReferences.Count; i++)
        {
            var (nodeReference, resolvedNodeId) = distinctReferences[i];

            var property = await _configuration.NodeMapper
                .TryGetPropertyAsync(registeredSubject, nodeReference, session, cancellationToken)
                .ConfigureAwait(false);

            if (property is not null)
            {
                childEntries.Add(new ChildEntry(nodeReference, resolvedNodeId, property, false));
                continue;
            }

            var dynamicPropertyName = nodeReference.BrowseName.Name;
            if (registeredSubject.TryGetProperty(dynamicPropertyName) is not null)
            {
                childEntries.Add(new ChildEntry(nodeReference, resolvedNodeId, null, false));
                continue;
            }

            var addAsDynamic = _configuration.ShouldAddDynamicProperty is not null &&
                await _configuration.ShouldAddDynamicProperty(nodeReference, cancellationToken).ConfigureAwait(false);

            if (!addAsDynamic)
            {
                childEntries.Add(new ChildEntry(nodeReference, resolvedNodeId, null, false));
                continue;
            }

            childEntries.Add(new ChildEntry(nodeReference, resolvedNodeId, null, true));
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

    private async Task<Dictionary<NodeId, Type>> ResolveObjectTypesAsync(IReadOnlyCollection<NodeId> objectNodeIds, OpcUaLoadContext context)
    {
        var objectTypeMap = new Dictionary<NodeId, Type>();
        if (objectNodeIds.Count == 0)
        {
            return objectTypeMap;
        }

        var objectBrowseResults = await context.BrowseAsync(objectNodeIds).ConfigureAwait(false);
        foreach (var nodeId in objectNodeIds)
        {
            // Missing entry = browse returned a bad status (BrowseNodesAsync deliberately
            // omits failed NodeIds so they aren't cached). Leave unset; TryCreateDynamicProperty
            // logs "Could not infer type" and skips, and the next load gets to retry.
            if (objectBrowseResults.TryGetValue(nodeId, out var children))
            {
                objectTypeMap[nodeId] = _configuration.TypeResolver.ResolveObjectNodeType(children);
            }
        }

        return objectTypeMap;
    }

    private async Task LoadChildPropertiesAsync(
        List<SubjectState> subjectStates,
        Dictionary<NodeId, Type> objectTypeMap,
        IReadOnlyDictionary<NodeId, Type?> variableTypeMap,
        OpcUaLoadContext context)
    {
        var allAttributeVariableNodes = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var allPendingSubjectRefs = new List<PendingSubjectRef>();
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
                            allPendingSubjectRefs.Add(new PendingSubjectRef(property, nodeReference, subjectToLoad, isNew));
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
        OpcUaLoadContext context)
    {
        if (context.SubjectsByNodeId.TryGetValue(resolvedNodeId, out var reusedSubject))
        {
            property.SetValueFromSource(_source, null, null, reusedSubject);
            return null;
        }

        var existingChildren = property.Children;
        var existingSubject = existingChildren.IsEmpty ? null : existingChildren[0].Subject;
        var subjectToLoad = existingSubject
            ?? await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeReference, context.Session, context.CancellationToken).ConfigureAwait(false);

        if (existingSubject is null)
        {
            subjectToLoad.Context.AddFallbackContext(parentSubject.Context);
        }

        context.SubjectsByNodeId.TryAdd(resolvedNodeId, subjectToLoad);
        return (subjectToLoad, existingSubject is null);
    }

    private async Task BatchLoadVariableNodesAsync(
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> variableNodes,
        OpcUaLoadContext context)
    {
        if (variableNodes.Count == 0)
        {
            return;
        }

        var nodesToBrowse = new List<(NodeId NodeId, RegisteredSubject ChildSubject, RegisteredSubjectProperty? ValueProperty)>();
        var nodeIds = new HashSet<NodeId>(variableNodes.Count);

        // Dedup by childSubject: when two parent properties reference the same Variable
        // subject (graph-shaped address space, not tree), the second pass would re-claim
        // the same value + sub-attribute references and log a spurious ownership error.
        var processedChildSubjects = new HashSet<RegisteredSubject>(variableNodes.Count);

        foreach (var (property, nodeId) in variableNodes)
        {
            var children = property.Children;
            var childSubject = (children.IsEmpty ? null : (IInterceptorSubject?)children[0].Subject)?.TryGetRegisteredSubject();
            if (childSubject is null)
            {
                _logger.LogWarning(
                    "Skipping OPC UA Variable-typed subject reference '{Subject}.{Property}' (NodeId: {NodeId}): no child subject was pre-constructed. The parent type must instantiate this property in its constructor.",
                    property.Subject.GetType().Name, property.Name, nodeId);
                continue;
            }

            if (!processedChildSubjects.Add(childSubject))
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

        var browseResults = await context.BrowseAsync(nodeIds).ConfigureAwait(false);

        foreach (var (nodeId, childSubject, valueProperty) in nodesToBrowse)
        {
            if (!browseResults.TryGetValue(nodeId, out var rawChildren))
            {
                continue;
            }

            var childNodes = context.DistinctByResolvedNodeId(rawChildren);
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
        OpcUaLoadContext context)
    {
        if (pendingCollections.Count + pendingDictionaries.Count == 0)
        {
            return;
        }

        var allNodeIds = new HashSet<NodeId>(pendingCollections.Count + pendingDictionaries.Count);
        foreach (var (_, nodeId) in pendingCollections) allNodeIds.Add(nodeId);
        foreach (var (_, nodeId) in pendingDictionaries) allNodeIds.Add(nodeId);

        var browseResults = await context.BrowseAsync(allNodeIds).ConfigureAwait(false);
        var allChildrenToLoad = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>();

        foreach (var (property, nodeId) in pendingCollections)
        {
            var rawChildren = browseResults.TryGetValue(nodeId, out var r) ? r : [];
            var childNodes = context.DistinctByResolvedNodeId(rawChildren);

            var children = await ResolveChildSubjectsAsync(property, childNodes, isDictionary: false, context).ConfigureAwait(false);

            var collection = DefaultSubjectFactory.Instance.CreateSubjectCollection(property.Type, children.Select(c => c.Subject));
            property.SetValueFromSource(_source, null, null, collection);
            allChildrenToLoad.AddRange(children);
        }

        foreach (var (property, nodeId) in pendingDictionaries)
        {
            var rawChildren = browseResults.TryGetValue(nodeId, out var r) ? r : [];
            var childNodes = context.DistinctByResolvedNodeId(rawChildren);

            var children = await ResolveChildSubjectsAsync(property, childNodes, isDictionary: true, context).ConfigureAwait(false);

            var entries = new Dictionary<object, IInterceptorSubject>(children.Count);
            foreach (var (node, subject) in children)
            {
                entries[ExtractDictionaryKey(node.BrowseName.Name)] = subject;
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
        OpcUaLoadContext context)
    {
        var existingChildren = property.Children;
        var existingByKey = new Dictionary<object, IInterceptorSubject?>(isDictionary ? existingChildren.Length : 0);
        if (isDictionary)
        {
            foreach (var existing in existingChildren)
            {
                if (existing.Index is { } index)
                {
                    existingByKey[index] = existing.Subject;
                }
            }
        }

        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(childNodes.Count);

        for (var i = 0; i < childNodes.Count; i++)
        {
            var (childNode, nodeId) = childNodes[i];

            IInterceptorSubject? childSubject;
            object factoryIndex;

            if (isDictionary)
            {
                var key = ExtractDictionaryKey(childNode.BrowseName.Name);
                existingByKey.TryGetValue(key, out childSubject);
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
        List<PendingSubjectRef> pendingRefs,
        OpcUaLoadContext context)
    {
        if (pendingRefs.Count == 0)
        {
            return;
        }

        var refsToLoad = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(pendingRefs.Count);
        foreach (var pending in pendingRefs)
        {
            refsToLoad.Add((pending.NodeReference, pending.SubjectToLoad));
        }
        await LoadSubjectsAsync(refsToLoad, context).ConfigureAwait(false);

        foreach (var pending in pendingRefs)
        {
            if (pending.IsNew)
            {
                pending.Property.SetValueFromSource(_source, null, null, pending.SubjectToLoad);
            }
        }
    }

    private async Task LoadAttributeNodesForManyAsync(
        IReadOnlyList<(RegisteredSubjectProperty Property, NodeId NodeId)> variableNodes,
        OpcUaLoadContext context)
    {
        if (variableNodes.Count == 0)
        {
            return;
        }

        // Tracks (property, parentNodeId) pairs already processed in any prior round, so
        // we never call MatchKnownAttributes / CollectDynamicAttributesAsync twice for the
        // same pair. A property could legitimately appear in multiple rounds against
        // different parent NodeIds, but processing the SAME pair twice would re-claim
        // already-monitored sub-attribute references.
        var processedEntries = new HashSet<(RegisteredSubjectProperty Property, NodeId ParentNodeId)>();
        var currentRound = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>(variableNodes);

        var maxTraversals = _configuration.MaxAttributeTraversals;
        var traversal = 0;
        while (currentRound.Count > 0)
        {
            if (++traversal > maxTraversals)
            {
                _logger.LogWarning(
                    "Aborting attribute traversal after {MaxTraversals} levels with {Remaining} entries still pending. Possible cycle in address space or attribute registration.",
                    maxTraversals, currentRound.Count);
                break;
            }

            currentRound = await ProcessAttributeRoundAsync(currentRound, processedEntries, context).ConfigureAwait(false);
        }
    }

    private async Task<List<(RegisteredSubjectProperty Property, NodeId NodeId)>> ProcessAttributeRoundAsync(
        List<(RegisteredSubjectProperty Property, NodeId ParentNodeId)> currentRound,
        HashSet<(RegisteredSubjectProperty Property, NodeId ParentNodeId)> processedEntries,
        OpcUaLoadContext context)
    {
        var parentIds = new HashSet<NodeId>(currentRound.Count);
        foreach (var (_, parentNodeId) in currentRound)
        {
            parentIds.Add(parentNodeId);
        }

        var browseResults = await context.BrowseAsync(parentIds).ConfigureAwait(false);

        var dynamicAttributeNodes = new List<DynamicAttributeNode>();
        var nextRound = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();

        foreach (var (property, parentNodeId) in currentRound)
        {
            if (!processedEntries.Add((property, parentNodeId)))
            {
                continue;
            }

            if (!browseResults.TryGetValue(parentNodeId, out var rawChildren) || rawChildren.Count == 0)
            {
                continue;
            }

            var childNodes = context.DistinctByResolvedNodeId(rawChildren);
            var processedBrowseNames = MatchKnownAttributes(property, childNodes, nextRound, context);
            await CollectDynamicAttributesAsync(property, parentNodeId, childNodes, processedBrowseNames, dynamicAttributeNodes, context).ConfigureAwait(false);
        }

        await ResolveDynamicAttributesAsync(dynamicAttributeNodes, nextRound, context).ConfigureAwait(false);
        return nextRound;
    }

    private HashSet<string> MatchKnownAttributes(
        RegisteredSubjectProperty property,
        List<(ReferenceDescription Reference, NodeId NodeId)> childNodes,
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> nextRound,
        OpcUaLoadContext context)
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
        List<DynamicAttributeNode> dynamicAttributeNodes,
        OpcUaLoadContext context)
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

            dynamicAttributeNodes.Add(new DynamicAttributeNode(property, childNodeId, childNode, browseName));
        }
    }

    private async Task ResolveDynamicAttributesAsync(
        List<DynamicAttributeNode> dynamicAttributeNodes,
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> nextRound,
        OpcUaLoadContext context)
    {
        if (dynamicAttributeNodes.Count == 0)
        {
            return;
        }

        var variableReferences = new List<ReferenceDescription>(dynamicAttributeNodes.Count);
        foreach (var entry in dynamicAttributeNodes)
        {
            variableReferences.Add(entry.ChildNode);
        }
        var resolvedTypes = await _configuration.TypeResolver.ResolveVariableTypesAsync(context.Session, variableReferences, context.CancellationToken).ConfigureAwait(false);

        foreach (var entry in dynamicAttributeNodes)
        {
            resolvedTypes.TryGetValue(entry.ChildNodeId, out var inferredType);
            if (inferredType is null)
            {
                _logger.LogWarning(
                    "Could not infer type for dynamic attribute '{AttributeName}' on property '{PropertyName}' (NodeId: {NodeId}). Skipping attribute.",
                    entry.BrowseName, entry.OwnerProperty.Name, entry.ChildNode.NodeId);
                continue;
            }

            object? value = null;
            var dynamicAttribute = entry.OwnerProperty.AddAttribute(
                entry.BrowseName,
                inferredType,
                _ => value,
                (_, o) => value = o,
                _configuration.TypeResolver.GetDynamicPropertyAttributes(entry.ChildNode, context.Session));

            MonitorValueNode(entry.ChildNodeId, dynamicAttribute, context.MonitoredItems);
            nextRound.Add((dynamicAttribute, entry.ChildNodeId));
        }
    }

    // Mirrors OpcUaTypeResolver.ResolveObjectNodeType: dictionary classification is
    // triggered by `Name[key]` browse names, so the dictionary's user-facing key is the
    // bracket content (`key`), not the literal browse name (`Name[key]`). Falls back to
    // the full browse name when no usable bracket suffix is present.
    private static string ExtractDictionaryKey(string browseName)
    {
        var bracketStart = browseName.LastIndexOf('[');
        if (bracketStart >= 0 && browseName.EndsWith("]"))
        {
            var contentLength = browseName.Length - bracketStart - 2;
            if (contentLength > 0)
            {
                return browseName.Substring(bracketStart + 1, contentLength);
            }
        }
        return browseName;
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

}

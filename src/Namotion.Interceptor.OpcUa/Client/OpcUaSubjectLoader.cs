using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Graph;
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
        await LoadSubjectAsync(subject, node, session, monitoredItems, loadedSubjects, cancellationToken).ConfigureAwait(false);
        return monitoredItems;
    }

    private async Task LoadSubjectAsync(IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
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

        var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, session.NamespaceUris);

        // Track subject with reference counter - only process on first reference
        var isFirstReference = _source.TrackSubject(subject, nodeId, () => []);
        if (!isFirstReference)
        {
            return;
        }

        var nodeReferences = await BrowseNodeAsync(nodeId, session, cancellationToken).ConfigureAwait(false);

        // Track which properties were matched to server nodes
        var claimedPropertyNames = new HashSet<string>();

        // Accumulate flat collection items for batch processing
        var flatCollectionItems = new Dictionary<RegisteredSubjectProperty, List<(int Index, ReferenceDescription Node)>>();

        for (var index = 0; index < nodeReferences.Count; index++)
        {
            var nodeReference = nodeReferences[index];
            var property = await FindSubjectPropertyAsync(registeredSubject, nodeReference, session, cancellationToken).ConfigureAwait(false);
            if (property is null)
            {
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
                    continue;
                }

                // Check for flat collection item (e.g., "Sensors[0]")
                if (OpcUaBrowseHelper.TryParseCollectionIndex(dynamicPropertyName, out var baseName, out var collectionIndex))
                {
                    RegisteredSubjectProperty? flatProperty = null;
                    foreach (var childProperty in registeredSubject.Properties)
                    {
                        var propertyBrowseName = childProperty.ResolvePropertyName(_configuration.NodeMapper);
                        if (propertyBrowseName == baseName && childProperty.IsSubjectCollection)
                        {
                            var nodeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(childProperty);
                            if (nodeConfiguration?.CollectionStructure == CollectionNodeStructure.Flat)
                            {
                                flatProperty = childProperty;
                                break;
                            }
                        }
                    }

                    if (flatProperty is not null)
                    {
                        // Accumulate flat collection items for batch processing
                        if (!flatCollectionItems.TryGetValue(flatProperty, out var items))
                        {
                            items = new List<(int Index, ReferenceDescription Node)>();
                            flatCollectionItems[flatProperty] = items;
                        }
                        items.Add((collectionIndex, nodeReference));
                        continue;
                    }
                }

                var addAsDynamic = _configuration.ShouldAddDynamicProperty is not null &&
                    await _configuration.ShouldAddDynamicProperty(nodeReference, cancellationToken).ConfigureAwait(false);

                if (!addAsDynamic)
                {
                    continue;
                }

                // Infer CLR type from OPC UA variable metadata if possible
                var inferredType = await _configuration.TypeResolver.TryGetTypeForNodeAsync(session, nodeReference, cancellationToken).ConfigureAwait(false);
                if (inferredType is null)
                {
                    _logger.LogWarning(
                        "Could not infer type for dynamic property '{PropertyName}' (NodeId: {NodeId}). Skipping property.",
                        dynamicPropertyName, nodeReference.NodeId);

                    continue;
                }

                object? value = null;
                property = registeredSubject.AddProperty(
                    dynamicPropertyName,
                    inferredType,
                    _ => value,
                    (_, o) => value = o,
                    CreateDynamicNodeAttribute(nodeReference, session));
            }

            var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
            if (propertyName is not null)
            {
                claimedPropertyNames.Add(property.Name);
                var childNodeId = ExpandedNodeId.ToNodeId(nodeReference.NodeId, session.NamespaceUris);

                if (property.IsSubjectReference)
                {
                    // Claim ownership of structural property so changes flow through ChangeQueueProcessor
                    _ownership.ClaimSource(property.Reference);

                    // Check if this should be treated as a VariableNode
                    var nodeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(property);
                    if (nodeConfiguration?.NodeClass == Mapping.OpcUaNodeClass.Variable)
                    {
                        await LoadVariableNodeForSubjectAsync(property, childNodeId, session, monitoredItems, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await LoadSubjectReferenceAsync(property, nodeReference, subject, session, monitoredItems, loadedSubjects, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (property.IsSubjectCollection)
                {
                    // Claim ownership of structural property so changes flow through ChangeQueueProcessor
                    _ownership.ClaimSource(property.Reference);

                    await LoadSubjectCollectionAsync(property, childNodeId, monitoredItems, session, loadedSubjects, cancellationToken).ConfigureAwait(false);
                }
                else if (property.IsSubjectDictionary)
                {
                    // Claim ownership of structural property so changes flow through ChangeQueueProcessor
                    _ownership.ClaimSource(property.Reference);

                    await LoadSubjectDictionaryAsync(property, childNodeId, monitoredItems, session, loadedSubjects, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    MonitorValueNode(childNodeId, property, monitoredItems);
                    var visitedNodes = new HashSet<NodeId>();
                    await LoadAttributeNodesAsync(property, childNodeId, session, monitoredItems, visitedNodes, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Process accumulated flat collection items (sorted by index)
        foreach (var (flatProperty, items) in flatCollectionItems)
        {
            claimedPropertyNames.Add(flatProperty.Name);
            _ownership.ClaimSource(flatProperty.Reference);

            // Sort by index and load each item
            var sortedItems = items.OrderBy(i => i.Index).ToList();
            var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(sortedItems.Count);

            for (var i = 0; i < sortedItems.Count; i++)
            {
                var (_, itemNode) = sortedItems[i];
                var childSubject = DefaultSubjectFactory.Instance.CreateSubjectForCollectionOrDictionaryProperty(flatProperty);
                childSubject.Context.AddFallbackContext(subject.Context);
                children.Add((itemNode, childSubject));
            }

            // Create and set the collection
            var collection = DefaultSubjectFactory.Instance
                .CreateSubjectCollection(flatProperty.Type, children.Select(c => c.Subject));
            flatProperty.SetValueFromSource(_source, null, null, collection);

            // Load each child subject
            foreach (var (childNode, childSubject) in children)
            {
                await LoadSubjectAsync(childSubject, childNode, session, monitoredItems, loadedSubjects, cancellationToken).ConfigureAwait(false);
            }
        }

        // Second pass: claim ownership of structural properties that weren't matched to server nodes
        // This enables Client → Server sync for new nodes that don't exist on the server yet
        foreach (var property in registeredSubject.Properties)
        {
            if (claimedPropertyNames.Contains(property.Name))
            {
                continue;
            }

            if (property.IsSubjectReference || property.IsSubjectCollection || property.IsSubjectDictionary)
            {
                _ownership.ClaimSource(property.Reference);
            }
        }
    }

    private async Task LoadAttributeNodesAsync(
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<NodeId> visitedNodes,
        CancellationToken cancellationToken)
    {
        // Guard against cycles in OPC UA hierarchy
        if (!visitedNodes.Add(parentNodeId))
            return;

        // Browse children of the variable node
        var childNodes = await BrowseNodeAsync(parentNodeId, session, cancellationToken).ConfigureAwait(false);
        var matchedNames = new HashSet<string>();

        // First pass: match known attributes from C# model
        foreach (var attribute in property.Attributes)
        {
            var attributeConfiguration = _configuration.NodeMapper.TryGetNodeConfiguration(attribute);
            if (attributeConfiguration is null)
                continue;

            var attributeBrowseName = attributeConfiguration.BrowseName ?? attribute.BrowseName;
            ReferenceDescription? matchingNode = null;
            foreach (var childNode in childNodes)
            {
                if (childNode.BrowseName.Name == attributeBrowseName)
                {
                    matchingNode = childNode;
                    break;
                }
            }

            if (matchingNode is null)
                continue;

            matchedNames.Add(attributeBrowseName);
            var attributeNodeId = ExpandedNodeId.ToNodeId(matchingNode.NodeId, session.NamespaceUris);
            MonitorValueNode(attributeNodeId, attribute, monitoredItems);

            // Recursive: attributes can have attributes
            await LoadAttributeNodesAsync(attribute, attributeNodeId, session, monitoredItems, visitedNodes, cancellationToken).ConfigureAwait(false);
        }

        // Second pass: add dynamic attributes (same pattern as ShouldAddDynamicProperty)
        foreach (var childNode in childNodes)
        {
            if (childNode.NodeClass != NodeClass.Variable)
                continue;

            var browseName = childNode.BrowseName.Name;
            if (matchedNames.Contains(browseName))
                continue;

            var addAsDynamic = _configuration.ShouldAddDynamicAttribute is not null &&
                await _configuration.ShouldAddDynamicAttribute(childNode, cancellationToken).ConfigureAwait(false);
            if (!addAsDynamic)
                continue;

            var inferredType = await _configuration.TypeResolver.TryGetTypeForNodeAsync(session, childNode, cancellationToken).ConfigureAwait(false);
            if (inferredType is null)
            {
                _logger.LogWarning(
                    "Could not infer type for dynamic attribute '{AttributeName}' on property '{PropertyName}' (NodeId: {NodeId}). Skipping attribute.",
                    browseName, property.Name, childNode.NodeId);
                continue;
            }

            // Same closure pattern as dynamic properties
            object? value = null;
            var dynamicAttribute = property.AddAttribute(
                browseName,
                inferredType,
                _ => value,
                (_, o) => value = o,
                CreateDynamicNodeAttribute(childNode, session));

            var attributeNodeId = ExpandedNodeId.ToNodeId(childNode.NodeId, session.NamespaceUris);
            MonitorValueNode(attributeNodeId, dynamicAttribute, monitoredItems);

            // Recursive with cycle detection
            await LoadAttributeNodesAsync(dynamicAttribute, attributeNodeId, session, monitoredItems, visitedNodes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static OpcUaNodeAttribute CreateDynamicNodeAttribute(ReferenceDescription nodeReference, ISession session)
    {
        var namespaceUri = nodeReference.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(nodeReference.NodeId.NamespaceIndex);
        return new OpcUaNodeAttribute(nodeReference.BrowseName.Name, namespaceUri)
        {
            NodeIdentifier = nodeReference.NodeId.Identifier.ToString(),
            NodeNamespaceUri = namespaceUri
        };
    }

    private async Task LoadSubjectReferenceAsync(RegisteredSubjectProperty property,
        ReferenceDescription nodeReference,
        IInterceptorSubject subject,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
        CancellationToken cancellationToken)
    {
        var existingSubject = property.Children.SingleOrDefault();
        if (existingSubject.Subject is not null)
        {
            await LoadSubjectAsync(existingSubject.Subject, nodeReference, session, monitoredItems, loadedSubjects, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Create new subject instance
            var newSubject = await _configuration.SubjectFactory.CreateSubjectForPropertyAsync(property, nodeReference, session, cancellationToken).ConfigureAwait(false);
            newSubject.Context.AddFallbackContext(subject.Context);
            await LoadSubjectAsync(newSubject, nodeReference, session, monitoredItems, loadedSubjects, cancellationToken).ConfigureAwait(false);
            property.SetValueFromSource(_source, null, null, newSubject);
        }
    }

    private async Task LoadSubjectCollectionAsync(RegisteredSubjectProperty property,
        NodeId childNodeId,
        List<MonitoredItem> monitoredItems,
        ISession session,
        HashSet<IInterceptorSubject> loadedSubjects,
        CancellationToken cancellationToken)
    {
        var childNodes = await BrowseNodeAsync(childNodeId, session, cancellationToken).ConfigureAwait(false);
        var childCount = childNodes.Count;
        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(childCount);

        // Convert to array once to avoid multiple enumerations
        var existingChildren = property.Children.ToArray();

        for (var i = 0; i < childCount; i++)
        {
            var childNode = childNodes[i];
            var childSubject = i < existingChildren.Length ? existingChildren[i].Subject : null;
            childSubject ??= DefaultSubjectFactory.Instance.CreateSubjectForCollectionOrDictionaryProperty(property);

            children.Add((childNode, childSubject));
        }

        var collection = DefaultSubjectFactory.Instance
            .CreateSubjectCollection(property.Type, children.Select(c => c.Subject));

        property.SetValueFromSource(_source, null, null, collection);

        // TODO(perf): Consider parallelizing child subject loading with Task.WhenAll.
        // Requires making monitoredItems and loadedSubjects thread-safe (e.g., ConcurrentBag, ConcurrentHashSet).
        foreach (var child in children)
        {
            await LoadSubjectAsync(child.Subject, child.Node, session, monitoredItems, loadedSubjects, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadSubjectDictionaryAsync(
        RegisteredSubjectProperty property,
        NodeId childNodeId,
        List<MonitoredItem> monitoredItems,
        ISession session,
        HashSet<IInterceptorSubject> loadedSubjects,
        CancellationToken cancellationToken)
    {
        var childNodes = await BrowseNodeAsync(childNodeId, session, cancellationToken).ConfigureAwait(false);
        var existingChildren = property.Children.ToDictionary(c => c.Index!, c => c.Subject);
        var entries = new Dictionary<object, IInterceptorSubject>();
        var nodesByKey = new Dictionary<object, ReferenceDescription>();

        foreach (var childNode in childNodes)
        {
            var key = childNode.BrowseName.Name; // Use BrowseName as dictionary key
            var childSubject = existingChildren.GetValueOrDefault(key)
                ?? DefaultSubjectFactory.Instance.CreateSubjectForCollectionOrDictionaryProperty(property);
            entries[key] = childSubject;
            nodesByKey[key] = childNode;
        }

        var dictionary = DefaultSubjectFactory.Instance.CreateSubjectDictionary(property.Type, entries);
        property.SetValueFromSource(_source, null, null, dictionary);

        foreach (var entry in entries)
        {
            var childNode = nodesByKey[entry.Key];
            await LoadSubjectAsync(entry.Value, childNode, session, monitoredItems, loadedSubjects, cancellationToken).ConfigureAwait(false);
        }
    }

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

        // Also track the monitored item with its owning subject for reference counting
        _source.AddMonitoredItemToSubject(property.Reference.Subject, monitoredItem);
    }

    private async Task LoadVariableNodeForSubjectAsync(
        RegisteredSubjectProperty property,
        NodeId nodeId,
        ISession session,
        List<MonitoredItem> monitoredItems,
        CancellationToken cancellationToken)
    {
        var childSubject = property.Children.SingleOrDefault().Subject?.TryGetRegisteredSubject();
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
        var childNodes = await BrowseNodeAsync(nodeId, session, cancellationToken).ConfigureAwait(false);
        foreach (var childNode in childNodes)
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
                    var childNodeId = ExpandedNodeId.ToNodeId(childNode.NodeId, session.NamespaceUris);
                    MonitorValueNode(childNodeId, childProperty, monitoredItems);
                    break;
                }
            }
        }
    }

    private async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        NodeId nodeId,
        ISession session,
        CancellationToken cancellationToken)
    {
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

        return results;
    }
}

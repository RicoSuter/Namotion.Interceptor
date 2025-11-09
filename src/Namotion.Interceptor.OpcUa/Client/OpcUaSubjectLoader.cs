using Microsoft.Extensions.Logging;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectLoader
{
    private const string OpcVariableKey = "OpcVariable";
    private const uint NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly List<PropertyReference> _propertiesWithOpcData;
    private readonly ISubjectSource _source;

    public OpcUaSubjectLoader(
        OpcUaClientConfiguration configuration,
        List<PropertyReference> propertiesWithOpcData,
        ISubjectSource source,
        ILogger logger)
    {
        _configuration = configuration;
        _propertiesWithOpcData = propertiesWithOpcData;
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
        await LoadSubjectAsync(subject, node, monitoredItems, session, cancellationToken);
        return monitoredItems;
    }

    private async Task LoadSubjectAsync(
        IInterceptorSubject subject,
        ReferenceDescription node,
        List<MonitoredItem> monitoredItems,
        ISession session,
        CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, session.NamespaceUris);
        var nodeRefs = await BrowseNodeAsync(nodeId, session, cancellationToken);
        
        for (var index = 0; index < nodeRefs.Count; index++)
        {
            var nodeRef = nodeRefs[index];
            var property = FindSubjectProperty(registeredSubject, nodeRef, session);
            if (property is null)
            {
                var dynamicPropertyName = nodeRef.BrowseName.Name;

                if (registeredSubject.Properties.Any(t => t.Name == dynamicPropertyName))
                {
                    continue;
                }

                var addAsDynamic = _configuration.ShouldAddDynamicProperty is not null &&
                    await _configuration.ShouldAddDynamicProperty(nodeRef, cancellationToken);

                if (!addAsDynamic)
                {
                    continue;
                }

                // Infer CLR type from OPC UA variable metadata if possible
                var inferredType = await _configuration.TypeResolver.TryGetTypeForNodeAsync(session, nodeRef, cancellationToken);
                if (inferredType is null)
                {
                    _logger.LogWarning(
                        "Could not infer type for dynamic property '{PropertyName}' (NodeId: {NodeId}). Skipping property.",
                        dynamicPropertyName, nodeRef.NodeId);

                    continue;
                }

                object? value = null;
                property = registeredSubject.AddProperty(
                    dynamicPropertyName,
                    inferredType,
                    _ => value,
                    (_, o) => value = o,
                    new OpcUaNodeAttribute(
                        nodeRef.BrowseName.Name,
                        nodeRef.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(nodeRef.NodeId.NamespaceIndex),
                        sourceName: null)
                    {
                        NodeIdentifier = nodeRef.NodeId.Identifier.ToString(),
                        NodeNamespaceUri = nodeRef.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(nodeRef.NodeId.NamespaceIndex)
                    });
            }

            var propertyName = property.ResolvePropertyName(_configuration.SourcePathProvider);
            if (propertyName is not null)
            {
                var childNodeId = ExpandedNodeId.ToNodeId(nodeRef.NodeId, session.NamespaceUris);

                if (property.IsSubjectReference)
                {
                    await LoadSubjectReferenceAsync(property, node, nodeRef, subject, monitoredItems, session, cancellationToken);
                }
                else if (property.IsSubjectCollection)
                {
                    await LoadSubjectCollectionAsync(property, childNodeId, monitoredItems, session, cancellationToken);
                }
                else if (property.IsSubjectDictionary)
                {
                    // TODO: Implement dictionary support
                }
                else
                {
                    MonitorValueNode(childNodeId, property, monitoredItems);
                }
            }
        }
    }

    private async Task LoadSubjectReferenceAsync(
        RegisteredSubjectProperty property,
        ReferenceDescription node,
        ReferenceDescription nodeRef,
        IInterceptorSubject subject,
        List<MonitoredItem> monitoredItems,
        ISession session,
        CancellationToken cancellationToken)
    {
        var existingSubject = property.Children.SingleOrDefault();
        if (existingSubject.Subject is not null)
        {
            await LoadSubjectAsync(existingSubject.Subject, node, monitoredItems, session, cancellationToken);
        }
        else
        {
            // Create new subject instance
            var newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeRef, session, cancellationToken);
            newSubject.Context.AddFallbackContext(subject.Context);
            await LoadSubjectAsync(newSubject, nodeRef, monitoredItems, session, cancellationToken);
            property.SetValueFromSource(_source, null, newSubject);
        }
    }

    private async Task LoadSubjectCollectionAsync(
        RegisteredSubjectProperty property,
        NodeId childNodeId,
        List<MonitoredItem> monitoredItems,
        ISession session,
        CancellationToken cancellationToken)
    {
        var childNodes = await BrowseNodeAsync(childNodeId, session, cancellationToken);
        var childCount = childNodes.Count;
        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(childCount);
        
        // Convert to array once to avoid multiple enumerations
        var existingChildren = property.Children.ToArray();
        
        for (var i = 0; i < childCount; i++)
        {
            var childNode = childNodes[i];
            var childSubject = i < existingChildren.Length ? existingChildren[i].Subject : null;
            childSubject ??= DefaultSubjectFactory.Instance.CreateCollectionSubject(property, i);

            children.Add((childNode, childSubject));
        }

        var collection = DefaultSubjectFactory.Instance
            .CreateSubjectCollection(property.Type, children.Select(c => c.Subject));

        property.SetValueFromSource(_source, null, collection);

        foreach (var child in children)
        {
            await LoadSubjectAsync(child.Subject, child.Node, monitoredItems, session, cancellationToken);
        }
    }

    private RegisteredSubjectProperty? FindSubjectProperty(RegisteredSubject registeredSubject, ReferenceDescription nodeRef, ISession session)
    {
        var nodeIdString = nodeRef.NodeId.Identifier.ToString();
        var nodeNamespaceUri = nodeRef.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(nodeRef.NodeId.NamespaceIndex);
        
        var properties = registeredSubject.Properties;
        foreach (var property in properties)
        {
            var opcUaNodeAttribute = property.TryGetOpcUaNodeAttribute();
            if (opcUaNodeAttribute is not null && opcUaNodeAttribute.NodeIdentifier == nodeIdString)
            {
                var propertyNodeNamespaceUri = opcUaNodeAttribute.NodeNamespaceUri
                    ?? _configuration.DefaultNamespaceUri
                    ?? throw new InvalidOperationException("No default namespace URI configured.");

                if (propertyNodeNamespaceUri == nodeNamespaceUri)
                {
                    return property;
                }
            }
        }

        return _configuration.SourcePathProvider.TryGetPropertyFromSegment(registeredSubject, nodeRef.BrowseName.Name);
    }

    private void MonitorValueNode(NodeId nodeId, RegisteredSubjectProperty property, List<MonitoredItem> monitoredItems)
    {
        var opcUaNodeAttribute = property.TryGetOpcUaNodeAttribute();
        var monitoredItem = new MonitoredItem
        {
            StartNodeId = nodeId,
            AttributeId = Opc.Ua.Attributes.Value,

            MonitoringMode = MonitoringMode.Reporting,
           
            SamplingInterval = opcUaNodeAttribute?.SamplingInterval ?? _configuration.DefaultSamplingInterval,
            QueueSize = opcUaNodeAttribute?.QueueSize ?? _configuration.DefaultQueueSize,
            DiscardOldest = opcUaNodeAttribute?.DiscardOldest ?? _configuration.DefaultDiscardOldest,

            // Delay ClientHandle mapping until after the item is added to a subscription.
            // Store the property on the item itself for later reference.
            Handle = property
        };

        property.Reference.SetPropertyData(OpcVariableKey, nodeId);
        _propertiesWithOpcData.Add(property.Reference);
        monitoredItems.Add(monitoredItem);
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
            cancellationToken);

        if (response.Results.Count > 0)
        {
            if (response.Results[0].References is { Count: > 0 } references)
            {
                foreach (var reference in references)
                {
                    results.Add(reference);
                }
            }

            var continuationPoint = response.Results[0].ContinuationPoint;
            while (continuationPoint is { Length: > 0 })
            {
                var nextResponse = await session.BrowseNextAsync(null, false, new ByteStringCollection { continuationPoint }, cancellationToken);
                if (nextResponse.Results.Count > 0)
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

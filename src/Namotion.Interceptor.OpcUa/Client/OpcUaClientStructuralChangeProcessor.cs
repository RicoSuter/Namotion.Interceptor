using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class OpcUaClientStructuralChangeProcessor : OpcUaStructuralChangeProcessor
{
    private readonly ConnectorSubjectMap<NodeId> _subjectMap;
    private readonly OpcUaSubjectLoader _loader;
    private readonly OpcUaSubjectClientSource _clientSource;
    private readonly OpcUaClientConfiguration _configuration;

    private volatile Session? _session;
    private volatile SubscriptionManager? _subscriptionManager;

    public ConnectorSubjectMap<NodeId> SubjectMap => _subjectMap;

    public OpcUaClientStructuralChangeProcessor(
        OpcUaSubjectLoader loader,
        OpcUaSubjectClientSource clientSource,
        OpcUaClientConfiguration configuration,
        ILogger logger)
        : base(logger)
    {
        _loader = loader;
        _clientSource = clientSource;
        _configuration = configuration;
        _subjectMap = new ConnectorSubjectMap<NodeId>(clientSource.RootSubject.Context);
    }

    public void SetSession(Session session, SubscriptionManager subscriptionManager)
    {
        _session = session;
        _subscriptionManager = subscriptionManager;
    }

    public Task RunAsync(CancellationToken cancellationToken) => ProcessLoopAsync(cancellationToken);

    protected override async Task ProcessEventAsync(StructuralChangeEvent evt, CancellationToken cancellationToken)
    {
        Logger.LogDebug(
            "Client structural event: {Verb} IsLocal={IsLocal} NodeId={NodeId} Subject={Subject}.",
            evt.Verb, evt.IsLocal, evt.AffectedNodeId, evt.Subject?.GetType().Name);

        if (evt.IsLocal)
        {
            if (evt.Verb == StructuralChangeVerb.Add)
            {
                await SendAddNodesToServerAsync(evt, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendDeleteNodesToServerAsync(evt, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            if (evt.Verb == StructuralChangeVerb.Add)
            {
                await ProcessExternalAddAsync(evt, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ProcessExternalRemove(evt);
            }
        }
    }

    private async Task SendAddNodesToServerAsync(StructuralChangeEvent evt, CancellationToken cancellationToken)
    {
        if (evt.Subject is null || evt.Property is null)
        {
            return;
        }

        var session = _session;
        if (session is null || !session.Connected)
        {
            return;
        }

        var parentSubject = evt.Property.Subject;
        if (!parentSubject.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var parentNodeIdObj) ||
            parentNodeIdObj is not NodeId parentNodeId)
        {
            return;
        }

        var propertyName = evt.Property.ResolvePropertyName(_configuration.NodeMapper);
        if (propertyName is null)
        {
            return;
        }

        var namespaceIndex = GetNamespaceIndex(session);
        NodeId containerNodeId;
        QualifiedName browseName;

        if (evt.Property.IsSubjectCollection || evt.Property.IsSubjectDictionary)
        {
            var parentPath = parentNodeId.Identifier is string stringId ? stringId : "";
            var containerPath = string.IsNullOrEmpty(parentPath)
                ? propertyName
                : parentPath + "." + propertyName;
            containerNodeId = new NodeId(containerPath, namespaceIndex);

            browseName = evt.Property.IsSubjectCollection
                ? new QualifiedName($"{propertyName}[{evt.Index}]", namespaceIndex)
                : new QualifiedName(evt.Index?.ToString() ?? "", namespaceIndex);
        }
        else
        {
            containerNodeId = parentNodeId;
            browseName = new QualifiedName(propertyName, namespaceIndex);
        }

        try
        {
            var response = await session.AddNodesAsync(
                requestHeader: null,
                [new AddNodesItem
                {
                    ParentNodeId = new ExpandedNodeId(containerNodeId),
                    ReferenceTypeId = ReferenceTypeIds.HasComponent,
                    RequestedNewNodeId = ExpandedNodeId.Null,
                    BrowseName = browseName,
                    NodeClass = NodeClass.Object,
                    NodeAttributes = new ExtensionObject(new ObjectAttributes
                    {
                        DisplayName = new LocalizedText(browseName.Name),
                        Description = LocalizedText.Null,
                        WriteMask = 0,
                        UserWriteMask = 0,
                        EventNotifier = 0
                    }),
                    TypeDefinition = new ExpandedNodeId(ObjectTypeIds.BaseObjectType)
                }],
                cancellationToken).ConfigureAwait(false);

            if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
            {
                evt.Subject.SetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, response.Results[0].AddedNodeId);
            }
        }
        catch (ServiceResultException ex) when (
            ex.StatusCode == StatusCodes.BadNotSupported ||
            ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            Logger.LogWarning("Server does not support AddNodes service.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send AddNodes for property {PropertyName}.", propertyName);
        }
    }

    private async Task SendDeleteNodesToServerAsync(StructuralChangeEvent evt, CancellationToken cancellationToken)
    {
        if (evt.AffectedNodeId is null)
        {
            return;
        }

        var session = _session;
        if (session is null || !session.Connected)
        {
            return;
        }

        try
        {
            await session.DeleteNodesAsync(
                requestHeader: null,
                [new DeleteNodesItem { NodeId = evt.AffectedNodeId, DeleteTargetReferences = true }],
                cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (
            ex.StatusCode == StatusCodes.BadNotSupported ||
            ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            Logger.LogWarning("Server does not support DeleteNodes service.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send DeleteNodes for NodeId {NodeId}.", evt.AffectedNodeId);
        }
    }

    private async Task ProcessExternalAddAsync(StructuralChangeEvent evt, CancellationToken cancellationToken)
    {
        var affectedNodeId = evt.AffectedNodeId;
        if (affectedNodeId is null)
        {
            return;
        }

        if (_subjectMap.TryGetSubject(affectedNodeId, out _))
        {
            return;
        }

        var session = _session;
        var subscriptionManager = _subscriptionManager;
        if (session is null || !session.Connected || subscriptionManager is null)
        {
            return;
        }

        NodeId? containerNodeId = null;
        var currentNodeId = affectedNodeId;
        ParentContext? context = null;

        for (var depth = 0; depth < 10; depth++)
        {
            var parentId = await BrowseParentAsync(currentNodeId, session, cancellationToken).ConfigureAwait(false);
            if (parentId is null)
            {
                return;
            }

            context = ResolveParentContext(parentId, containerNodeId: containerNodeId);
            if (context is not null)
            {
                break;
            }

            containerNodeId = parentId;
            currentNodeId = parentId;
        }

        if (context is null)
        {
            return;
        }

        var browseNodeId = context.Value.ContainerNodeId ?? context.Value.ParentNodeId;
        var children = await _loader.BrowseNodeAsync(browseNodeId, session, cancellationToken).ConfigureAwait(false);
        ReferenceDescription? affectedRef = null;
        foreach (var child in children)
        {
            var childNodeId = ExpandedNodeId.ToNodeId(child.NodeId, session.NamespaceUris);
            if (childNodeId is not null && childNodeId == affectedNodeId)
            {
                affectedRef = child;
                break;
            }
        }

        if (affectedRef is null)
        {
            return;
        }

        var registered = context.Value.ParentSubject.TryGetRegisteredSubject();
        if (registered is null)
        {
            return;
        }

        var (targetProperty, index) = FindTargetProperty(registered, context.Value, affectedRef, children);
        if (targetProperty is null)
        {
            return;
        }

        IInterceptorSubject newSubject;
        if (targetProperty.IsSubjectCollection || targetProperty.IsSubjectDictionary)
        {
            newSubject = await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                targetProperty, affectedRef, index, session, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            newSubject = await _configuration.SubjectFactory.CreateSubjectAsync(
                targetProperty, affectedRef, session, cancellationToken).ConfigureAwait(false);
        }

        newSubject.Context.AddFallbackContext(context.Value.ParentSubject.Context);

        var monitoredItems = await _loader.LoadSubjectAsync(
            newSubject, affectedRef, session, _subjectMap, cancellationToken).ConfigureAwait(false);

        AddSubjectToProperty(targetProperty, newSubject, index);

        if (monitoredItems.Count > 0)
        {
            await subscriptionManager.AddMonitoredItemsAsync(
                monitoredItems, (Session)session, cancellationToken).ConfigureAwait(false);

            await ReadInitialValuesAsync(monitoredItems, session, cancellationToken).ConfigureAwait(false);
        }
    }

    private (RegisteredSubjectProperty? Property, object? Index) FindTargetProperty(
        RegisteredSubject registered,
        ParentContext context,
        ReferenceDescription affectedRef,
        ReferenceDescriptionCollection children)
    {
        foreach (var property in registered.Properties)
        {
            if (!property.CanContainSubjects)
            {
                continue;
            }

            var name = property.ResolvePropertyName(_configuration.NodeMapper);
            if (name is null)
            {
                continue;
            }

            if (context.ContainerNodeId is not null)
            {
                if (context.ContainerBrowseName == name)
                {
                    object? index = null;
                    if (property.IsSubjectCollection)
                    {
                        index = children.IndexOf(affectedRef);
                    }
                    else if (property.IsSubjectDictionary)
                    {
                        index = affectedRef.BrowseName.Name;
                    }
                    return (property, index);
                }
            }
            else if (property.IsSubjectReference && name == affectedRef.BrowseName.Name)
            {
                return (property, null);
            }
        }

        return (null, null);
    }

    private void ProcessExternalRemove(StructuralChangeEvent evt)
    {
        if (evt.AffectedNodeId is null)
        {
            return;
        }

        if (!_subjectMap.TryGetSubject(evt.AffectedNodeId, out var subject) || subject is null)
        {
            return;
        }

        var registered = subject.TryGetRegisteredSubject();
        if (registered is null)
        {
            return;
        }

        var parents = registered.Parents;
        if (parents.Length == 0)
        {
            return;
        }

        var parent = parents[0];
        RemoveSubjectFromProperty(parent.Property, subject);
        _subjectMap.Remove(evt.AffectedNodeId);
    }

    protected override bool TryGetNodeIdForSubject(IInterceptorSubject subject, out NodeId? nodeId)
    {
        if (subject.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var obj) && obj is NodeId id)
        {
            nodeId = id;
            return true;
        }

        nodeId = null;
        return false;
    }

    public void PopulateSubjectMap(IInterceptorSubject subject)
    {
        var visited = new HashSet<IInterceptorSubject>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        PopulateRecursive(subject, visited);
    }

    private void PopulateRecursive(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
    {
        if (!visited.Add(subject))
        {
            return;
        }

        if (TryGetNodeIdForSubject(subject, out var nodeId) && nodeId is not null)
        {
            _subjectMap.Add(nodeId, subject);
        }

        var registered = subject.TryGetRegisteredSubject();
        if (registered is null)
        {
            return;
        }

        foreach (var property in registered.Properties)
        {
            if (!property.CanContainSubjects)
            {
                continue;
            }

            foreach (var child in property.Children)
            {
                if (child.Subject is not null)
                {
                    PopulateRecursive(child.Subject, visited);
                }
            }
        }
    }

    private async Task ReadInitialValuesAsync(
        IReadOnlyList<MonitoredItem> monitoredItems,
        ISession session,
        CancellationToken cancellationToken)
    {
        var readValues = new ReadValueIdCollection(monitoredItems.Count);
        foreach (var item in monitoredItems)
        {
            readValues.Add(new ReadValueId
            {
                NodeId = item.StartNodeId,
                AttributeId = Opc.Ua.Attributes.Value
            });
        }

        var readResponse = await session.ReadAsync(
            requestHeader: null,
            maxAge: 0,
            timestampsToReturn: TimestampsToReturn.Source,
            readValues,
            cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < Math.Min(readResponse.Results.Count, monitoredItems.Count); i++)
        {
            if (StatusCode.IsGood(readResponse.Results[i].StatusCode) &&
                monitoredItems[i].Handle is RegisteredSubjectProperty property)
            {
                var dataValue = readResponse.Results[i];
                var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property);
                property.SetValueFromSource(_clientSource, dataValue.SourceTimestamp, null, value);
            }
        }
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _subjectMap.Dispose();
        return default;
    }

    private readonly record struct ParentContext(
        IInterceptorSubject ParentSubject,
        NodeId ParentNodeId,
        NodeId? ContainerNodeId,
        string? ContainerBrowseName);

    private ParentContext? ResolveParentContext(NodeId candidateNodeId, NodeId? containerNodeId = null)
    {
        if (!_subjectMap.TryGetSubject(candidateNodeId, out var parentSubject) || parentSubject is null)
        {
            return null;
        }

        string? containerBrowseName = null;
        if (containerNodeId is not null)
        {
            containerBrowseName = containerNodeId.Identifier is string s
                ? s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..] : s
                : null;
        }

        return new ParentContext(parentSubject, candidateNodeId, containerNodeId, containerBrowseName);
    }

    private async Task<NodeId?> BrowseParentAsync(NodeId nodeId, ISession session, CancellationToken cancellationToken)
    {
        var (_, _, results, _) = await session.BrowseAsync(
            requestHeader: null,
            view: null,
            [nodeId],
            maxResultsToReturn: 1u,
            BrowseDirection.Inverse,
            ReferenceTypeIds.HierarchicalReferences,
            includeSubtypes: true,
            (uint)(NodeClass.Object | NodeClass.Variable),
            cancellationToken).ConfigureAwait(false);

        if (results.Count > 0 && results[0].Count > 0)
        {
            return ExpandedNodeId.ToNodeId(results[0][0].NodeId, session.NamespaceUris);
        }

        return null;
    }

    private void AddSubjectToProperty(RegisteredSubjectProperty property, IInterceptorSubject newSubject, object? index)
    {
        if (property.IsSubjectReference)
        {
            property.SetValueFromSource(_clientSource, null, null, newSubject);
        }
        else if (property.IsSubjectCollection)
        {
            var current = property.Children.Where(c => c.Subject is not null).Select(c => c.Subject!).ToList();
            current.Add(newSubject);
            var collection = DefaultSubjectFactory.Instance.CreateSubjectCollection(property.Type, current);
            property.SetValueFromSource(_clientSource, null, null, collection);
        }
        else if (property.IsSubjectDictionary && index is not null)
        {
            var entries = new Dictionary<object, IInterceptorSubject>();
            foreach (var child in property.Children)
            {
                if (child.Index is not null && child.Subject is not null)
                {
                    entries[child.Index] = child.Subject;
                }
            }
            entries[index] = newSubject;
            var dictionary = DefaultSubjectFactory.Instance.CreateSubjectDictionary(property.Type, entries);
            property.SetValueFromSource(_clientSource, null, null, dictionary);
        }
    }

    private void RemoveSubjectFromProperty(RegisteredSubjectProperty property, IInterceptorSubject subject)
    {
        if (property.IsSubjectReference)
        {
            property.SetValueFromSource(_clientSource, null, null, null);
        }
        else if (property.IsSubjectCollection)
        {
            var remaining = property.Children
                .Where(c => c.Subject is not null && !ReferenceEquals(c.Subject, subject))
                .Select(c => c.Subject!).ToList();
            var collection = DefaultSubjectFactory.Instance.CreateSubjectCollection(property.Type, remaining);
            property.SetValueFromSource(_clientSource, null, null, collection);
        }
        else if (property.IsSubjectDictionary)
        {
            var entries = new Dictionary<object, IInterceptorSubject>();
            foreach (var child in property.Children)
            {
                if (child.Index is not null && child.Subject is not null && !ReferenceEquals(child.Subject, subject))
                {
                    entries[child.Index] = child.Subject;
                }
            }
            var dictionary = DefaultSubjectFactory.Instance.CreateSubjectDictionary(property.Type, entries);
            property.SetValueFromSource(_clientSource, null, null, dictionary);
        }
    }

    private static ushort GetNamespaceIndex(ISession session)
    {
        var index = session.NamespaceUris.GetIndex("http://namotion.com/Interceptor/");
        return index >= 0 ? (ushort)index : (ushort)0;
    }
}

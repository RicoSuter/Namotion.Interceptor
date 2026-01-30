using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Processes structural property changes (add/remove subjects) for OPC UA client.
/// Creates or removes MonitoredItems when the C# model changes.
/// Optionally calls AddNodes/DeleteNodes on the server when EnableRemoteNodeManagement is enabled.
/// Note: Source filtering (loop prevention) is handled by ChangeQueueProcessor, not here.
/// </summary>
internal class OpcUaClientStructuralChangeProcessor : StructuralChangeProcessor
{
    private readonly OpcUaSubjectClientSource _source;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly OpcUaSubjectLoader _subjectLoader;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpcUaClientStructuralChangeProcessor"/> class.
    /// </summary>
    /// <param name="source">The OPC UA client source for tracking subjects.</param>
    /// <param name="configuration">The client configuration.</param>
    /// <param name="subjectLoader">The subject loader for loading new subjects.</param>
    /// <param name="logger">The logger.</param>
    public OpcUaClientStructuralChangeProcessor(
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
    /// Gets or sets the current OPC UA session. Must be set before processing changes.
    /// </summary>
    internal ISession? CurrentSession { get; set; }

    /// <inheritdoc />
    protected override async Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
    {
        var session = CurrentSession;
        if (session is null)
        {
            _logger.LogWarning(
                "Cannot create MonitoredItems for added subject on property '{PropertyName}': no active session.",
                property.Name);
            return;
        }

        // Check if subject is already tracked (shared subject scenario)
        if (_source.IsSubjectTracked(subject))
        {
            // Subject already tracked - just increment reference count
            if (_source.TryGetSubjectNodeId(subject, out var existingNodeId) && existingNodeId is not null)
            {
                _source.TrackSubject(subject, existingNodeId, () => []);
            }
            return;
        }

        // Determine the parent NodeId for browsing
        NodeId? parentNodeId = null;
        var parentSubject = property.Parent.Subject;

        if (_source.TryGetSubjectNodeId(parentSubject, out var parentNode) && parentNode is not null)
        {
            parentNodeId = parentNode;
        }
        else if (ReferenceEquals(parentSubject, _source.RootSubject))
        {
            // Parent is root - browse from ObjectsFolder or RootName
            parentNodeId = _configuration.RootName is not null
                ? await TryFindRootNodeIdAsync(session, CancellationToken.None).ConfigureAwait(false)
                : ObjectIds.ObjectsFolder;
        }

        if (parentNodeId is null)
        {
            _logger.LogWarning(
                "Cannot create MonitoredItems for added subject on property '{PropertyName}': parent node not found.",
                property.Name);
            return;
        }

        // Find the child node by browsing
        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
        if (propertyName is null)
        {
            return;
        }

        var childNodeRef = await TryFindChildNodeAsync(session, parentNodeId, propertyName, index, property.IsSubjectCollection, CancellationToken.None).ConfigureAwait(false);
        var wasCreatedRemotely = false;

        if (childNodeRef is null)
        {
            // Node not found on server - try to create it if remote node management is enabled
            if (_configuration.EnableRemoteNodeManagement)
            {
                childNodeRef = await TryCreateRemoteNodeAsync(session, parentNodeId, property, subject, index, CancellationToken.None).ConfigureAwait(false);
                wasCreatedRemotely = childNodeRef is not null;
            }

            if (childNodeRef is null)
            {
                return;
            }
        }

        // Load the subject and create MonitoredItems
        var monitoredItems = await _subjectLoader.LoadSubjectAsync(subject, childNodeRef, session, CancellationToken.None).ConfigureAwait(false);

        if (monitoredItems.Count > 0)
        {
            var sessionManager = _source.SessionManager;
            if (sessionManager is not null)
            {
                await sessionManager.AddMonitoredItemsAsync(monitoredItems, session, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // If we created the node remotely, write the initial property values from the client's subject
        if (wasCreatedRemotely)
        {
            await WriteInitialPropertyValuesAsync(subject, session, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override Task OnSubjectRemovedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
    {
        // Cleanup is handled by SourceOwnershipManager.OnSubjectDetaching callback in OpcUaSubjectClientSource
        return Task.CompletedTask;
    }

    private async Task<NodeId?> TryFindRootNodeIdAsync(ISession session, CancellationToken cancellationToken)
    {
        if (_configuration.RootName is null)
        {
            return ObjectIds.ObjectsFolder;
        }

        var references = await OpcUaBrowseHelper.BrowseNodeAsync(session, ObjectIds.ObjectsFolder, cancellationToken).ConfigureAwait(false);
        foreach (var reference in references)
        {
            if (reference.BrowseName.Name == _configuration.RootName)
            {
                return ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            }
        }

        return null;
    }

    private async Task<ReferenceDescription?> TryFindChildNodeAsync(
        ISession session,
        NodeId parentNodeId,
        string propertyName,
        object? index,
        bool isCollection,
        CancellationToken cancellationToken)
    {
        var references = await OpcUaBrowseHelper.BrowseNodeAsync(session, parentNodeId, cancellationToken).ConfigureAwait(false);

        // For collections/dictionaries, first find the container node
        if (isCollection || index is not null)
        {
            // Find container first
            NodeId? containerNodeId = null;
            foreach (var reference in references)
            {
                if (reference.BrowseName.Name == propertyName)
                {
                    containerNodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
                    break;
                }
            }

            if (containerNodeId is null)
            {
                return null;
            }

            // Browse container for specific item
            var containerChildren = await OpcUaBrowseHelper.BrowseNodeAsync(session, containerNodeId, cancellationToken).ConfigureAwait(false);
            var expectedBrowseName = isCollection ? $"{propertyName}[{index}]" : index?.ToString();

            foreach (var child in containerChildren)
            {
                if (child.BrowseName.Name == expectedBrowseName)
                {
                    return child;
                }
            }

            return null;
        }

        // For single references, find direct child
        foreach (var reference in references)
        {
            if (reference.BrowseName.Name == propertyName)
            {
                return reference;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to create a remote node on the OPC UA server via AddNodes service.
    /// </summary>
    private async Task<ReferenceDescription?> TryCreateRemoteNodeAsync(
        ISession session,
        NodeId parentNodeId,
        RegisteredSubjectProperty property,
        IInterceptorSubject subject,
        object? index,
        CancellationToken cancellationToken)
    {
        // Get the TypeDefinition from the subject's type attribute
        var subjectType = subject.GetType();
        var typeAttribute = subjectType.GetCustomAttributes(typeof(OpcUaNodeAttribute), inherit: true)
            .OfType<OpcUaNodeAttribute>()
            .FirstOrDefault();

        // Resolve the TypeDefinition NodeId
        ExpandedNodeId typeDefinitionId;
        if (typeAttribute?.TypeDefinition is not null)
        {
            // TypeDefinition specified in attribute - try to resolve to a well-known type first
            var resolvedTypeId = ResolveWellKnownTypeDefinition(typeAttribute.TypeDefinition);
            if (resolvedTypeId is not null)
            {
                typeDefinitionId = new ExpandedNodeId(resolvedTypeId);
            }
            else
            {
                // Not a well-known type - use the string as-is with namespace
                var typeDefinitionNamespaceUri = typeAttribute.TypeDefinitionNamespace ?? Namespaces.OpcUa;
                var namespaceIndex = session.NamespaceUris.GetIndex(typeDefinitionNamespaceUri);
                if (namespaceIndex < 0)
                {
                    namespaceIndex = 0; // Default to OPC UA namespace
                }
                typeDefinitionId = new ExpandedNodeId(typeAttribute.TypeDefinition, (ushort)namespaceIndex);
            }
        }
        else if (_configuration.TypeRegistry is not null)
        {
            // Try to get TypeDefinition from client-side type registry
            var typeDefNodeId = _configuration.TypeRegistry.GetTypeDefinition(subjectType);
            if (typeDefNodeId is null)
            {
                _logger.LogWarning(
                    "Cannot create remote node for type '{TypeName}': no TypeDefinition attribute or registry entry.",
                    subjectType.Name);
                return null;
            }
            typeDefinitionId = new ExpandedNodeId(typeDefNodeId);
        }
        else
        {
            // Default to BaseObjectType if nothing else specified
            typeDefinitionId = ObjectTypeIds.BaseObjectType;
        }

        // Determine the browse name for the new node
        var propertyName = property.ResolvePropertyName(_configuration.NodeMapper);
        if (propertyName is null)
        {
            return null;
        }

        string browseName;
        NodeId actualParentNodeId;

        if (property.IsSubjectCollection)
        {
            // For collections: parent is the container node, browse name is PropertyName[index]
            actualParentNodeId = await TryFindContainerNodeAsync(session, parentNodeId, propertyName, cancellationToken).ConfigureAwait(false);
            if (NodeId.IsNull(actualParentNodeId))
            {
                _logger.LogWarning(
                    "Cannot create remote node: container node '{PropertyName}' not found.",
                    propertyName);
                return null;
            }
            browseName = $"{propertyName}[{index}]";
        }
        else if (index is not null)
        {
            // For dictionaries: parent is the container node, browse name is the key
            actualParentNodeId = await TryFindContainerNodeAsync(session, parentNodeId, propertyName, cancellationToken).ConfigureAwait(false);
            if (NodeId.IsNull(actualParentNodeId))
            {
                _logger.LogWarning(
                    "Cannot create remote node: container node '{PropertyName}' not found.",
                    propertyName);
                return null;
            }
            browseName = index.ToString() ?? propertyName;
        }
        else
        {
            // For single references: parent is the direct parent, browse name is the property name
            actualParentNodeId = parentNodeId;
            browseName = propertyName;
        }

        // Create the AddNodesItem
        var namespaceUri = _configuration.DefaultNamespaceUri ?? Namespaces.OpcUa;
        var browseNamespaceIndex = session.NamespaceUris.GetIndex(namespaceUri);
        if (browseNamespaceIndex < 0)
        {
            browseNamespaceIndex = 0;
        }

        var addNodesItem = new AddNodesItem
        {
            ParentNodeId = new ExpandedNodeId(actualParentNodeId),
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            RequestedNewNodeId = ExpandedNodeId.Null,
            BrowseName = new QualifiedName(browseName, (ushort)browseNamespaceIndex),
            NodeClass = NodeClass.Object,
            TypeDefinition = typeDefinitionId,
            NodeAttributes = new ExtensionObject(new ObjectAttributes
            {
                SpecifiedAttributes = (uint)NodeAttributesMask.DisplayName,
                DisplayName = new LocalizedText(browseName)
            })
        };

        var nodesToAdd = new AddNodesItemCollection { addNodesItem };

        try
        {
            var response = await session.AddNodesAsync(
                null,
                nodesToAdd,
                cancellationToken).ConfigureAwait(false);

            if (response.Results.Count > 0)
            {
                var result = response.Results[0];
                if (StatusCode.IsGood(result.StatusCode))
                {
                    _logger.LogDebug(
                        "Created remote node '{BrowseName}' with NodeId '{NodeId}'.",
                        browseName, result.AddedNodeId);

                    // Return a ReferenceDescription for the newly created node
                    return new ReferenceDescription
                    {
                        NodeId = result.AddedNodeId,
                        BrowseName = new QualifiedName(browseName, (ushort)browseNamespaceIndex),
                        DisplayName = new LocalizedText(browseName),
                        NodeClass = NodeClass.Object,
                        TypeDefinition = typeDefinitionId
                    };
                }

                _logger.LogWarning(
                    "AddNodes failed for '{BrowseName}': {StatusCode}.",
                    browseName, result.StatusCode);
            }
        }
        catch (ServiceResultException ex)
        {
            _logger.LogWarning(ex,
                "AddNodes service call failed for '{BrowseName}'.",
                browseName);
        }

        return null;
    }

    /// <summary>
    /// Finds a container node for collections/dictionaries.
    /// </summary>
    private async Task<NodeId> TryFindContainerNodeAsync(
        ISession session,
        NodeId parentNodeId,
        string containerName,
        CancellationToken cancellationToken)
    {
        var references = await OpcUaBrowseHelper.BrowseNodeAsync(session, parentNodeId, cancellationToken).ConfigureAwait(false);
        foreach (var reference in references)
        {
            if (reference.BrowseName.Name == containerName)
            {
                return ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            }
        }

        return NodeId.Null;
    }

    /// <summary>
    /// Resolves well-known OPC UA type definition names to their NodeIds.
    /// </summary>
    private static NodeId? ResolveWellKnownTypeDefinition(string typeDefinition)
    {
        return typeDefinition switch
        {
            "BaseObjectType" => ObjectTypeIds.BaseObjectType,
            "FolderType" => ObjectTypeIds.FolderType,
            "BaseDataVariableType" => VariableTypeIds.BaseDataVariableType,
            "PropertyType" => VariableTypeIds.PropertyType,
            "AnalogItemType" => VariableTypeIds.AnalogItemType,
            "DataItemType" => VariableTypeIds.DataItemType,
            "DiscreteItemType" => VariableTypeIds.DiscreteItemType,
            "TwoStateDiscreteType" => VariableTypeIds.TwoStateDiscreteType,
            "MultiStateDiscreteType" => VariableTypeIds.MultiStateDiscreteType,
            "ArrayItemType" => VariableTypeIds.ArrayItemType,
            _ => null
        };
    }

    /// <summary>
    /// Writes the initial property values from a client subject to the server after creating a remote node.
    /// This ensures property values like FirstName="ClientAdded" are transferred to the server.
    /// </summary>
    private async Task WriteInitialPropertyValuesAsync(
        IInterceptorSubject subject,
        ISession session,
        CancellationToken cancellationToken)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        var writeValues = new WriteValueCollection();

        foreach (var property in registeredSubject.Properties)
        {
            // Skip structural properties (collections, dictionaries, references)
            if (property.IsSubjectCollection || property.IsSubjectDictionary || property.IsSubjectReference)
            {
                continue;
            }

            // Skip attribute properties
            if (property.IsAttribute)
            {
                continue;
            }

            // Get the NodeId from property data (set by LoadSubjectAsync)
            if (!property.Reference.TryGetPropertyData(_source.OpcUaNodeIdKey, out var nodeIdObj) ||
                nodeIdObj is not NodeId propertyNodeId)
            {
                continue;
            }

            // Get the current value from the client's subject
            var value = property.GetValue();
            var convertedValue = _configuration.ValueConverter.ConvertToNodeValue(value, property);

            writeValues.Add(new WriteValue
            {
                NodeId = propertyNodeId,
                AttributeId = Opc.Ua.Attributes.Value,
                Value = new DataValue
                {
                    Value = convertedValue,
                    StatusCode = StatusCodes.Good,
                    SourceTimestamp = DateTime.UtcNow
                }
            });
        }

        if (writeValues.Count == 0)
        {
            return;
        }

        try
        {
            var response = await session.WriteAsync(
                requestHeader: null,
                writeValues,
                cancellationToken).ConfigureAwait(false);

            var successCount = 0;
            for (var i = 0; i < response.Results.Count; i++)
            {
                if (StatusCode.IsGood(response.Results[i]))
                {
                    successCount++;
                }
                else
                {
                    _logger.LogWarning(
                        "Failed to write initial value for node '{NodeId}': {StatusCode}.",
                        writeValues[i].NodeId, response.Results[i]);
                }
            }

            _logger.LogDebug(
                "Wrote {SuccessCount}/{TotalCount} initial property values for newly created subject.",
                successCount, writeValues.Count);
        }
        catch (ServiceResultException ex)
        {
            _logger.LogWarning(ex,
                "Write service call failed for initial property values.");
        }
    }
}

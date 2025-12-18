using System.Collections.Concurrent;
using System.Reflection;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class CustomNodeManager : CustomNodeManager2
{
    private const string PathDelimiter = ".";

    private readonly IInterceptorSubject _subject;
    private readonly OpcUaSubjectServerBackgroundService _source;
    private readonly OpcUaServerConfiguration _configuration;

    private readonly ConcurrentDictionary<RegisteredSubject, NodeState> _subjects = new();

    public CustomNodeManager(
        IInterceptorSubject subject,
        OpcUaSubjectServerBackgroundService source,
        IServerInternal server,
        ApplicationConfiguration applicationConfiguration,
        OpcUaServerConfiguration configuration) :
        base(server, applicationConfiguration, configuration.GetNamespaceUris())
    {
        _subject = subject;
        _source = source;
        _configuration = configuration;
    }

    protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
    {
        var collection = base.LoadPredefinedNodes(context);
        _configuration.LoadPredefinedNodes(collection, context);
        return collection;
    }

    public void ClearPropertyData()
    {
        foreach (var node in PredefinedNodes.Values)
        {
            if (node is BaseDataVariableState { Handle: PropertyReference property })
            {
                property.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
            }
        }
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        base.CreateAddressSpace(externalReferences);

        var registeredSubject = _subject.TryGetRegisteredSubject();
        if (registeredSubject is not null)
        {
            if (_configuration.RootName is not null)
            {
                var node = CreateFolderNode(ObjectIds.ObjectsFolder, 
                    new NodeId(_configuration.RootName, NamespaceIndex), _configuration.RootName, null, null);

                CreateObjectNode(node.NodeId, registeredSubject, _configuration.RootName + PathDelimiter);
            }
            else
            {
                CreateObjectNode(ObjectIds.ObjectsFolder, registeredSubject, string.Empty);
            }
        }
    }

    /// <summary>
    /// Removes nodes for a detached subject at runtime.
    /// Removes nodes from the address space and cleans up tracking.
    /// </summary>
    /// <param name="subject">The subject whose nodes should be removed</param>
    /// <returns>True if nodes were removed, false if subject wasn't tracked</returns>
    public bool RemoveDynamicSubjectNodes(IInterceptorSubject subject)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return false;
        }

        bool removed = false;
        
        lock (Lock)
        {
            // Find and remove the node from tracking
            if (_subjects.TryRemove(registeredSubject, out var nodeState))
            {
                try
                {
                    // Remove the node from the address space
                    // Find parent and remove reference
                    var nodeId = nodeState.NodeId;
                    
                    // Remove from PredefinedNodes if it exists there
                    if (PredefinedNodes.ContainsKey(nodeId))
                    {
                        PredefinedNodes.Remove(nodeId);
                    }
                    
                    // Clean up property data
                    var properties = registeredSubject.Properties;
                    foreach (var property in properties)
                    {
                        property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                    }
                    
                    removed = true;
                }
                catch
                {
                    // Even if removal fails, we've cleaned up tracking
                    removed = true;
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Removes nodes for a detached subject. Idempotent - safe to call multiple times.
    /// Legacy method that only cleans up tracking.
    /// For runtime removal with address space updates, use RemoveDynamicSubjectNodes.
    /// </summary>
    public void RemoveSubjectNodes(IInterceptorSubject subject)
    {
        foreach (var kvp in _subjects)
        {
            if (kvp.Key.Subject == subject)
            {
                _subjects.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Dynamically creates OPC UA nodes for a subject at runtime.
    /// Used when subjects are attached after server initialization.
    /// </summary>
    /// <param name="subject">The subject to create nodes for</param>
    /// <param name="parentNodeId">Optional parent node ID. If null, uses ObjectsFolder or configured root</param>
    /// <param name="pathPrefix">Optional path prefix for node identification</param>
    /// <returns>The created node, or null if subject is already registered</returns>
    public NodeState? CreateDynamicSubjectNodes(IInterceptorSubject subject, NodeId? parentNodeId = null, string? pathPrefix = null)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return null;
        }

        // Check if already created
        if (_subjects.ContainsKey(registeredSubject))
        {
            return _subjects[registeredSubject];
        }

        // Determine parent node and path
        var effectiveParentNodeId = parentNodeId ?? (_configuration.RootName is not null
            ? new NodeId(_configuration.RootName, NamespaceIndex)
            : ObjectIds.ObjectsFolder);

        var effectivePath = pathPrefix ?? (_configuration.RootName is not null
            ? _configuration.RootName + PathDelimiter
            : string.Empty);

        // Create nodes for the subject
        lock (Lock)
        {
            CreateObjectNode(effectiveParentNodeId, registeredSubject, effectivePath);
            
            // Return the created node
            _subjects.TryGetValue(registeredSubject, out var node);
            return node;
        }
    }

    private void CreateObjectNode(NodeId parentNodeId, RegisteredSubject subject, string prefix)
    {
        foreach (var property in subject.Properties)
        {
            var propertyName = property.ResolvePropertyName(_configuration.PathProvider);
            if (propertyName is not null)
            {
                if (property.IsSubjectCollection)
                {
                    CreateArrayObjectNode(propertyName, property, property.Children, parentNodeId, prefix);
                }
                else if (property.IsSubjectDictionary)
                {
                    CreateDictionaryObjectNode(propertyName, property, property.Children, parentNodeId, prefix);
                }
                else if (property.IsSubjectReference)
                {
                    var referencedSubject = property.Children.SingleOrDefault();
                    if (referencedSubject.Subject is not null)
                    {
                        CreateReferenceObjectNode(propertyName, property, referencedSubject, parentNodeId, prefix);
                    }
                }
                else 
                {
                    CreateVariableNode(propertyName, property, parentNodeId, prefix);
                }
            }
        }
    }

    private void CreateReferenceObjectNode(string propertyName, RegisteredSubjectProperty property, SubjectPropertyChild child, NodeId parentNodeId, string parentPath)
    {
        var path = parentPath + propertyName;
        var browseName = GetBrowseName(propertyName, property, child.Index);
        var referenceTypeId = GetReferenceTypeId(property);

        CreateChildObject(property, browseName, child.Subject, path, parentNodeId, referenceTypeId);
    }

    private void CreateArrayObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        var nodeId = GetNodeId(property, parentPath + propertyName);
        var browseName = GetBrowseName(propertyName, property, null);

        var typeDefinitionId = GetTypeDefinitionId(property);
        var referenceTypeId = GetReferenceTypeId(property);

        var propertyNode = CreateFolderNode(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);

        // Child objects below the array folder use path: parentPath + propertyName + "[index]"
        var childReferenceTypeId = GetChildReferenceTypeId(property);
        foreach (var child in children)
        {
            var childBrowseName = new QualifiedName($"{propertyName}[{child.Index}]", NamespaceIndex);
            var childPath = $"{parentPath}{propertyName}[{child.Index}]";

            CreateChildObject(property, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private void CreateDictionaryObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        var nodeId = GetNodeId(property, parentPath + propertyName);
        var browseName = GetBrowseName(propertyName, property, null);

        var typeDefinitionId = GetTypeDefinitionId(property);
        var referenceTypeId = GetReferenceTypeId(property);

        var propertyNode = CreateFolderNode(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);
        var childReferenceTypeId = GetChildReferenceTypeId(property);
        foreach (var child in children)
        {
            var childBrowseName = new QualifiedName(child.Index?.ToString(), NamespaceIndex);
            var childPath = parentPath + propertyName + PathDelimiter + child.Index;

            CreateChildObject(property, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private void CreateVariableNode(string propertyName, RegisteredSubjectProperty property, NodeId parentNodeId, string parentPath)
    {
        var value = _configuration.ValueConverter.ConvertToNodeValue(property.GetValue(), property);
        var typeInfo = _configuration.ValueConverter.GetNodeTypeInfo(property.Type);

        var nodeId = GetNodeId(property, parentPath + propertyName);
        var browseName = GetBrowseName(propertyName, property, null);

        var referenceTypeId = GetReferenceTypeId(property);
        var variableNode = CreateVariableNode(parentNodeId, nodeId, browseName, typeInfo, referenceTypeId);
        variableNode.Handle = property.Reference;

        // Adjust access according to property setter
        if (!property.HasSetter)
        {
            variableNode.AccessLevel = AccessLevels.CurrentRead;
            variableNode.UserAccessLevel = AccessLevels.CurrentRead;
        }
        else
        {
            variableNode.AccessLevel = AccessLevels.CurrentReadOrWrite;
            variableNode.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
        }

        // Set array dimensions (works for 1D and multi-D)
        if (value is Array arrayValue)
        {
            variableNode.ArrayDimensions = new ReadOnlyList<uint>(
                Enumerable.Range(0, arrayValue.Rank)
                    .Select(i => (uint)arrayValue.GetLength(i))
                    .ToArray());
        }

        variableNode.Value = value;
        variableNode.StateChanged += (_, _, changes) =>
        {
            if (changes.HasFlag(NodeStateChangeMasks.Value))
            {
                // Lock on node to prevent race conditions with WriteToSourceAsync

                DateTimeOffset timestamp;
                object? nodeValue;
                lock (variableNode)
                {
                    timestamp = variableNode.Timestamp;
                    nodeValue = variableNode.Value;
                }

                _source.UpdateProperty(property.Reference, timestamp, nodeValue);
            }
        };

        property.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, variableNode);
    }

    private NodeId GetNodeId(RegisteredSubjectProperty property, string fullPath)
    {
        var opcUaNodeAttribute = property.TryGetOpcUaNodeAttribute();
        if (opcUaNodeAttribute is { NodeIdentifier: not null })
        {
            return opcUaNodeAttribute.NodeNamespaceUri is not null ? 
                NodeId.Create(opcUaNodeAttribute.NodeIdentifier, opcUaNodeAttribute.NodeNamespaceUri, SystemContext.NamespaceUris) : 
                new NodeId(opcUaNodeAttribute.NodeIdentifier, NamespaceIndex);
        }

        return new NodeId(fullPath, NamespaceIndex);
    }

    private void CreateChildObject(RegisteredSubjectProperty property, QualifiedName browseName,
        IInterceptorSubject subject,
        string path,
        NodeId parentNodeId,
        NodeId? referenceTypeId)
    {
        var registeredSubject = subject.TryGetRegisteredSubject() ?? throw new InvalidOperationException("Registered subject not found.");

        if (_subjects.TryGetValue(registeredSubject, out var existingNode))
        {
            // Subject already created, add reference to existing node
            var parentNode = FindNodeInAddressSpace(parentNodeId);
            parentNode.AddReference(referenceTypeId ?? ReferenceTypeIds.HasComponent, false, existingNode.NodeId);
        }
        else
        {
            // Create new node and add to dictionary (thread-safe)
            _subjects.GetOrAdd(registeredSubject, _ =>
            {
                var nodeId = GetNodeId(property, path);
                var typeDefinitionId = GetTypeDefinitionId(subject);

                var node = CreateObjectNode(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);
                CreateObjectNode(node.NodeId, registeredSubject, path + PathDelimiter);

                return node;
            });
        }
    }

    private static NodeId? GetReferenceTypeId(RegisteredSubjectProperty property)
    {
        var referenceTypeAttribute = property.ReflectionAttributes
            .OfType<OpcUaNodeReferenceTypeAttribute>()
            .FirstOrDefault();

        return referenceTypeAttribute is not null ? typeof(ReferenceTypeIds).GetField(referenceTypeAttribute.Type)?.GetValue(null) as NodeId : null;
    }

    private static NodeId? GetChildReferenceTypeId(RegisteredSubjectProperty property)
    {
        var referenceTypeAttribute = property.ReflectionAttributes
            .OfType<OpcUaNodeItemReferenceTypeAttribute>()
            .FirstOrDefault();

        return referenceTypeAttribute is not null ? typeof(ReferenceTypeIds).GetField(referenceTypeAttribute.Type)?.GetValue(null) as NodeId : null;
    }

    private NodeId? GetTypeDefinitionId(RegisteredSubjectProperty property)
    {
        var typeDefinitionAttribute = property.ReflectionAttributes
            .OfType<OpcUaTypeDefinitionAttribute>()
            .FirstOrDefault();

        return GetTypeDefinitionId(typeDefinitionAttribute);
    }

    private NodeId? GetTypeDefinitionId(IInterceptorSubject subject)
    {
        var typeDefinitionAttribute = subject.GetType().GetCustomAttribute<OpcUaTypeDefinitionAttribute>();
        return GetTypeDefinitionId(typeDefinitionAttribute);
    }

    private NodeId? GetTypeDefinitionId(OpcUaTypeDefinitionAttribute? typeDefinitionAttribute)
    {
        if (typeDefinitionAttribute is null)
        {
            return null;
        }

        if (typeDefinitionAttribute.Namespace is not null)
        {
            var typeDefinition = NodeId.Create(
                typeDefinitionAttribute.Type,
                typeDefinitionAttribute.Namespace,
                SystemContext.NamespaceUris);

            return PredefinedNodes.Values.SingleOrDefault(n =>
                    n.BrowseName.Name == typeDefinition.Identifier.ToString() &&
                    n.BrowseName.NamespaceIndex == typeDefinition.NamespaceIndex)?
                .NodeId;
        }

        return typeof(ObjectTypeIds).GetField(typeDefinitionAttribute.Type)?.GetValue(null) as NodeId;
    }

    private QualifiedName GetBrowseName(string propertyName, RegisteredSubjectProperty property, object? index)
    {
        var browseNameProvider = property.TryGetOpcUaNodeAttribute();
        if (browseNameProvider is null)
        {
            return new QualifiedName(propertyName + (index is not null ? $"[{index}]" : string.Empty), NamespaceIndex);
        }

        if (browseNameProvider.BrowseNamespaceUri is not null)
        {
            return new QualifiedName(browseNameProvider.BrowseName, (ushort)SystemContext.NamespaceUris.GetIndex(browseNameProvider.BrowseNamespaceUri));
        }

        return new QualifiedName(browseNameProvider.BrowseName, NamespaceIndex);
    }

    private FolderState CreateFolderNode(
        NodeId parentNodeId,
        NodeId nodeId,
        QualifiedName browseName,
        NodeId? typeDefinition,
        NodeId? referenceType)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var folderNode = new FolderState(parentNode)
        {
            NodeId = nodeId,
            BrowseName = browseName,
            DisplayName = new LocalizedText(browseName.Name),
            TypeDefinitionId = typeDefinition ?? ObjectTypeIds.FolderType,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasComponent
        };

        parentNode?.AddChild(folderNode);

        AddPredefinedNode(SystemContext, folderNode);
        return folderNode;
    }
    
    private BaseObjectState CreateObjectNode(
        NodeId parentNodeId,
        NodeId nodeId,
        QualifiedName browseName,
        NodeId? typeDefinition,
        NodeId? referenceType)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var objectNode = new BaseObjectState(parentNode)
        {
            NodeId = nodeId,
            BrowseName = browseName,
            DisplayName = new LocalizedText(browseName.Name),
            TypeDefinitionId = typeDefinition ?? ObjectTypeIds.BaseObjectType,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasComponent
        };

        parentNode?.AddChild(objectNode);

        AddPredefinedNode(SystemContext, objectNode);
        return objectNode;
    }

    private BaseDataVariableState CreateVariableNode(
        NodeId parentNodeId, NodeId nodeId, QualifiedName browseName,
        Opc.Ua.TypeInfo dataType, NodeId? referenceType)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var variable = new BaseDataVariableState(parentNode)
        {
            NodeId = nodeId,

            SymbolicName = browseName.Name,
            BrowseName = browseName,
            DisplayName = new LocalizedText(browseName.Name),

            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            DataType = Opc.Ua.TypeInfo.GetDataTypeId(dataType),
            ValueRank = dataType.ValueRank,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow, // TODO: Is using now correct here?

            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasProperty
        };

        parentNode?.AddChild(variable);

        AddPredefinedNode(SystemContext, variable);
        return variable;
    }
}
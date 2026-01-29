using System.Reflection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
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
    private readonly IOpcUaNodeMapper _nodeMapper;
    private readonly ILogger _logger;
    private readonly OpcUaNodeFactory _nodeFactory;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private readonly ConnectorReferenceCounter<NodeState> _subjectRefCounter = new();

    public CustomNodeManager(
        IInterceptorSubject subject,
        OpcUaSubjectServerBackgroundService source,
        IServerInternal server,
        ApplicationConfiguration applicationConfiguration,
        OpcUaServerConfiguration configuration,
        ILogger logger) :
        base(server, applicationConfiguration, configuration.GetNamespaceUris())
    {
        _subject = subject;
        _source = source;
        _configuration = configuration;
        _nodeMapper = configuration.NodeMapper;
        _logger = logger;
        _nodeFactory = new OpcUaNodeFactory(logger);
    }

    // Expose protected members for OpcUaNodeFactory
    internal ISystemContext GetSystemContext() => SystemContext;
    internal NodeIdDictionary<NodeState> GetPredefinedNodes() => PredefinedNodes;
    internal NodeState FindNode(NodeId nodeId) => FindNodeInAddressSpace(nodeId);
    internal void AddNode(NodeState node) => AddPredefinedNode(SystemContext, node);

    protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
    {
        var collection = base.LoadPredefinedNodes(context);
        _configuration.LoadPredefinedNodes(collection, context);
        return collection;
    }

    public void ClearPropertyData()
    {
        var rootSubject = _subject.TryGetRegisteredSubject();
        if (rootSubject != null)
        {
            foreach (var property in rootSubject.Properties)
            {
                property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                ClearAttributePropertyData(property);
            }
        }

        foreach (var subject in _subjectRefCounter.GetAllSubjects())
        {
            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject != null)
            {
                foreach (var property in registeredSubject.Properties)
                {
                    property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                    ClearAttributePropertyData(property);
                }
            }
        }
    }

    private void ClearAttributePropertyData(RegisteredSubjectProperty property)
    {
        foreach (var attribute in property.Attributes)
        {
            attribute.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
            ClearAttributePropertyData(attribute);
        }
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        base.CreateAddressSpace(externalReferences);

        _structureLock.Wait();
        try
        {
            var registeredSubject = _subject.TryGetRegisteredSubject();
            if (registeredSubject is not null)
            {
                if (_configuration.RootName is not null)
                {
                    var node = _nodeFactory.CreateFolderNode(this, ObjectIds.ObjectsFolder,
                        new NodeId(_configuration.RootName, NamespaceIndex), new QualifiedName(_configuration.RootName, NamespaceIndex), null, null, null);

                    CreateSubjectNodes(node.NodeId, registeredSubject, _configuration.RootName + PathDelimiter);
                }
                else
                {
                    CreateSubjectNodes(ObjectIds.ObjectsFolder, registeredSubject, string.Empty);
                }
            }
        }
        finally
        {
            _structureLock.Release();
        }
    }

    /// <summary>
    /// Removes nodes for a detached subject. Idempotent - safe to call multiple times.
    /// Uses DeleteNode to properly cleanup nodes and event handlers, preventing memory leaks.
    /// Uses reference counting - only deletes the node when the last reference is removed.
    /// </summary>
    public void RemoveSubjectNodes(IInterceptorSubject subject)
    {
        _structureLock.Wait();
        try
        {
            // Decrement reference count and check if this was the last reference
            var isLast = _subjectRefCounter.DecrementAndCheckLast(subject, out var nodeState);

            if (isLast && nodeState is not null)
            {
                var registeredSubject = subject.TryGetRegisteredSubject();

                // Remove variable nodes for this subject's properties
                if (registeredSubject != null)
                {
                    foreach (var property in registeredSubject.Properties)
                    {
                        if (property.Reference.TryGetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, out var node)
                            && node is BaseDataVariableState variableNode)
                        {
                            DeleteNode(SystemContext, variableNode.NodeId);
                            property.Reference.RemovePropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey);
                        }
                    }
                }

                // Remove object node
                DeleteNode(SystemContext, nodeState.NodeId);
            }
        }
        finally
        {
            _structureLock.Release();
        }
    }

    /// <summary>
    /// Creates a node for a subject that was added to a property at runtime.
    /// Determines parent node and path from the property context.
    /// </summary>
    /// <param name="property">The property that the subject was added to.</param>
    /// <param name="subject">The subject to create a node for.</param>
    /// <param name="index">The collection/dictionary index, or null for single references.</param>
    public void CreateSubjectNode(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
    {
        _structureLock.Wait();
        try
        {
            // Find parent subject's node to determine where to attach the new node
            var parentSubject = property.Parent.Subject;
            NodeId parentNodeId;
            string parentPath;

            if (ReferenceEquals(parentSubject, _subject))
            {
                // Parent is the root subject
                if (_configuration.RootName is not null)
                {
                    parentNodeId = new NodeId(_configuration.RootName, NamespaceIndex);
                    parentPath = _configuration.RootName + PathDelimiter;
                }
                else
                {
                    parentNodeId = ObjectIds.ObjectsFolder;
                    parentPath = string.Empty;
                }
            }
            else if (_subjectRefCounter.TryGetData(parentSubject, out var parentNode) && parentNode is not null)
            {
                // Parent subject has an existing node
                parentNodeId = parentNode.NodeId;
                // Extract path from NodeId if it's a string identifier
                parentPath = parentNode.NodeId.Identifier is string stringId
                    ? stringId + PathDelimiter
                    : string.Empty;
            }
            else
            {
                // Parent node not found - can't create child node
                _logger.LogWarning(
                    "Cannot create node for subject on property '{PropertyName}': parent node not found.",
                    property.Name);
                return;
            }

            var propertyName = property.ResolvePropertyName(_nodeMapper);
            if (propertyName is null)
            {
                return;
            }

            var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);

            if (property.IsSubjectCollection)
            {
                // For collections, we need to find or create the container folder first
                var containerNodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, parentPath + propertyName);
                var containerNode = FindNodeInAddressSpace(containerNodeId);

                if (containerNode is null)
                {
                    // Container doesn't exist yet - create it
                    var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, null);
                    var typeDefinitionId = _nodeFactory.GetTypeDefinitionId(this, nodeConfiguration);
                    var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);
                    containerNode = _nodeFactory.CreateFolderNode(this, parentNodeId, containerNodeId, browseName, typeDefinitionId, referenceTypeId, nodeConfiguration);
                }

                // Create child under container
                var childIndex = index ?? property.Children.Length - 1;
                var childBrowseName = new QualifiedName($"{propertyName}[{childIndex}]", NamespaceIndex);
                var childPath = $"{parentPath}{propertyName}[{childIndex}]";
                var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(this, nodeConfiguration);

                CreateChildObject(property, childBrowseName, subject, childPath, containerNode.NodeId, childReferenceTypeId);
            }
            else if (property.IsSubjectDictionary)
            {
                // For dictionaries, similar to collections but with key-based naming
                var containerNodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, parentPath + propertyName);
                var containerNode = FindNodeInAddressSpace(containerNodeId);

                if (containerNode is null)
                {
                    // Container doesn't exist yet - create it
                    var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, null);
                    var typeDefinitionId = _nodeFactory.GetTypeDefinitionId(this, nodeConfiguration);
                    var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);
                    containerNode = _nodeFactory.CreateFolderNode(this, parentNodeId, containerNodeId, browseName, typeDefinitionId, referenceTypeId, nodeConfiguration);
                }

                var indexString = index?.ToString();
                if (string.IsNullOrEmpty(indexString))
                {
                    _logger.LogWarning(
                        "Dictionary property '{PropertyName}' has a child with null or empty key. Skipping OPC UA node creation.",
                        propertyName);
                    return;
                }

                var childBrowseName = new QualifiedName(indexString, NamespaceIndex);
                var childPath = parentPath + propertyName + PathDelimiter + index;
                var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(this, nodeConfiguration);

                CreateChildObject(property, childBrowseName, subject, childPath, containerNode.NodeId, childReferenceTypeId);
            }
            else if (property.IsSubjectReference)
            {
                // Single subject reference
                if (nodeConfiguration?.NodeClass == OpcUaNodeClass.Variable)
                {
                    CreateVariableNodeForSubject(propertyName, property, parentNodeId, parentPath);
                }
                else
                {
                    var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, index);
                    var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);
                    var path = parentPath + propertyName;
                    CreateChildObject(property, browseName, subject, path, parentNodeId, referenceTypeId);
                }
            }
        }
        finally
        {
            _structureLock.Release();
        }
    }

    private void CreateSubjectNodes(NodeId parentNodeId, RegisteredSubject subject, string prefix)
    {
        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute)
                continue;

            var propertyName = property.ResolvePropertyName(_nodeMapper);
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
                        // Check if this should be a VariableNode instead of ObjectNode
                        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
                        if (nodeConfiguration?.NodeClass == OpcUaNodeClass.Variable)
                        {
                            CreateVariableNodeForSubject(propertyName, property, parentNodeId, prefix);
                        }
                        else
                        {
                            CreateReferenceObjectNode(propertyName, property, referencedSubject, parentNodeId, prefix);
                        }
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
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, child.Index);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);

        CreateChildObject(property, browseName, child.Subject, path, parentNodeId, referenceTypeId);
    }

    private void CreateArrayObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        // Cache node configuration to avoid repeated lookups
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);

        var nodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, parentPath + propertyName);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, null);

        var typeDefinitionId = _nodeFactory.GetTypeDefinitionId(this, nodeConfiguration);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);

        var propertyNode = _nodeFactory.CreateFolderNode(this, parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId, nodeConfiguration);

        // Child objects below the array folder use path: parentPath + propertyName + "[index]"
        var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(this, nodeConfiguration);
        foreach (var child in children)
        {
            var childBrowseName = new QualifiedName($"{propertyName}[{child.Index}]", NamespaceIndex);
            var childPath = $"{parentPath}{propertyName}[{child.Index}]";

            CreateChildObject(property, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private void CreateDictionaryObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        // Cache node configuration to avoid repeated lookups
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);

        var nodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, parentPath + propertyName);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, null);

        var typeDefinitionId = _nodeFactory.GetTypeDefinitionId(this, nodeConfiguration);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);

        var propertyNode = _nodeFactory.CreateFolderNode(this, parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId, nodeConfiguration);
        var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(this, nodeConfiguration);
        foreach (var child in children)
        {
            var indexString = child.Index?.ToString();
            if (string.IsNullOrEmpty(indexString))
            {
                _logger.LogWarning(
                    "Dictionary property '{PropertyName}' contains a child with null or empty key. Skipping OPC UA node creation.",
                    propertyName);

                continue;
            }

            var childBrowseName = new QualifiedName(indexString, NamespaceIndex);
            var childPath = parentPath + propertyName + PathDelimiter + child.Index;

            CreateChildObject(property, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private BaseDataVariableState CreateVariableNode(
        string propertyName,
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        string parentPath,
        RegisteredSubjectProperty? configurationProperty = null)
    {
        // Use configurationProperty for node identity, property for value/type
        var actualConfigurationProperty = configurationProperty ?? property;

        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(actualConfigurationProperty);
        var nodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, parentPath + propertyName);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, nodeConfiguration, null);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, nodeConfiguration);
        var dataTypeOverride = _nodeFactory.GetDataTypeOverride(this, _nodeMapper.TryGetNodeConfiguration(property));

        var variableNode = ConfigureVariableNode(property, parentNodeId, nodeId, browseName, referenceTypeId, dataTypeOverride, nodeConfiguration);

        property.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, variableNode);

        CreateAttributeNodes(variableNode, property, parentPath + propertyName);
        return variableNode;
    }

    private void CreateAttributeNodes(NodeState parentNode, RegisteredSubjectProperty property, string parentPath)
    {
        foreach (var attribute in property.Attributes)
        {
            var attributeConfiguration = _nodeMapper.TryGetNodeConfiguration(attribute);
            if (attributeConfiguration is null)
                continue;

            var attributeName = attributeConfiguration.BrowseName ?? attribute.BrowseName;
            var attributePath = parentPath + PathDelimiter + attributeName;
            var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, attributeConfiguration) ?? ReferenceTypeIds.HasProperty;

            // Create variable node for attribute
            var attributeNode = CreateVariableNodeForAttribute(
                attributeName,
                attribute,
                parentNode.NodeId,
                attributePath,
                referenceTypeId);

            // Recursive: attributes can have attributes
            CreateAttributeNodes(attributeNode, attribute, attributePath);
        }
    }

    private BaseDataVariableState CreateVariableNodeForAttribute(
        string attributeName,
        RegisteredSubjectProperty attribute,
        NodeId parentNodeId,
        string path,
        NodeId referenceTypeId)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(attribute);
        var nodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, path);
        var browseName = _nodeFactory.GetBrowseName(this, attributeName, nodeConfiguration, null);
        var dataTypeOverride = _nodeFactory.GetDataTypeOverride(this, nodeConfiguration);

        var variableNode = ConfigureVariableNode(attribute, parentNodeId, nodeId, browseName, referenceTypeId, dataTypeOverride, nodeConfiguration);

        attribute.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, variableNode);

        return variableNode;
    }

    /// <summary>
    /// Shared helper that configures a variable node with value, access levels, array dimensions, and state change handler.
    /// </summary>
    private BaseDataVariableState ConfigureVariableNode(
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        NodeId nodeId,
        QualifiedName browseName,
        NodeId? referenceTypeId,
        NodeId? dataTypeOverride,
        OpcUaNodeConfiguration? nodeConfiguration)
    {
        var value = _configuration.ValueConverter.ConvertToNodeValue(property.GetValue(), property);
        var typeInfo = _configuration.ValueConverter.GetNodeTypeInfo(property.Type);

        var variableNode = _nodeFactory.CreateVariableNode(this, parentNodeId, nodeId, browseName, typeInfo, referenceTypeId, dataTypeOverride, nodeConfiguration);
        _nodeFactory.AddAdditionalReferences(this, variableNode, nodeConfiguration);
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

        return variableNode;
    }

    private void CreateVariableNodeForSubject(string propertyName, RegisteredSubjectProperty property, NodeId parentNodeId, string parentPath)
    {
        // Get the child subject - skip if null (structural sync will handle later)
        var childSubject = property.Children.SingleOrDefault().Subject?.TryGetRegisteredSubject();
        if (childSubject is null)
        {
            return;
        }

        // Find the [OpcUaValue] property
        var valueProperty = childSubject.TryGetValueProperty(_nodeMapper);
        if (valueProperty is null)
        {
            return;
        }

        // Create the variable node: value from valueProperty, config from containing property
        var variableNode = CreateVariableNode(propertyName, valueProperty, parentNodeId, parentPath, configurationProperty: property);

        // Create child properties of the VariableNode (excluding the value property)
        var path = parentPath + propertyName;
        foreach (var childProperty in childSubject.Properties)
        {
            var childConfig = _nodeMapper.TryGetNodeConfiguration(childProperty);
            if (childConfig?.IsValue != true)
            {
                var childName = childProperty.ResolvePropertyName(_nodeMapper);
                if (childName != null)
                {
                    CreateVariableNode(childName, childProperty, variableNode.NodeId, path + PathDelimiter);
                }
            }
        }
    }

    private void CreateChildObject(RegisteredSubjectProperty property, QualifiedName browseName,
        IInterceptorSubject subject,
        string path,
        NodeId parentNodeId,
        NodeId? referenceTypeId)
    {
        var registeredSubject = subject.TryGetRegisteredSubject() ?? throw new InvalidOperationException("Registered subject not found.");

        var isFirst = _subjectRefCounter.IncrementAndCheckFirst(subject, () =>
        {
            // Create new node (only called on first reference, protected by _structureLock)
            var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);
            var nodeId = _nodeFactory.GetNodeId(this, nodeConfiguration, path);
            var typeDefinitionId = GetTypeDefinitionIdForSubject(subject);

            var node = _nodeFactory.CreateObjectNode(this, parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId, nodeConfiguration);
            _nodeFactory.AddAdditionalReferences(this, node, nodeConfiguration);
            return node;
        }, out var nodeState);

        if (isFirst)
        {
            // First reference - recursively create child nodes
            CreateSubjectNodes(nodeState.NodeId, registeredSubject, path + PathDelimiter);
        }
        else
        {
            // Subject already created, add reference from parent to existing node
            var parentNode = FindNodeInAddressSpace(parentNodeId);
            parentNode.AddReference(referenceTypeId ?? ReferenceTypeIds.HasComponent, false, nodeState.NodeId);
        }
    }

    private NodeId? GetTypeDefinitionIdForSubject(IInterceptorSubject subject)
    {
        // For subjects, check if type has OpcUaNode attribute at class level
        var typeAttribute = subject.GetType().GetCustomAttribute<OpcUaNodeAttribute>();
        return _nodeFactory.GetTypeDefinitionId(this, typeAttribute);
    }
}
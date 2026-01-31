using System.Reflection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Server.Graph;

/// <summary>
/// Creates OPC UA nodes from model subjects and properties.
/// Extracted from CustomNodeManager for better separation of concerns.
/// </summary>
internal class NodeCreator
{
    private const string PathDelimiter = ".";

    private readonly CustomNodeManager _nodeManager;
    private readonly OpcUaServerConfiguration _configuration;
    private readonly IOpcUaNodeMapper _nodeMapper;
    private readonly OpcUaNodeFactory _nodeFactory;
    private readonly OpcUaSubjectServerBackgroundService _source;
    private readonly ConnectorReferenceCounter<NodeState> _subjectRefCounter;
    private readonly ModelChangePublisher _modelChangePublisher;
    private readonly ILogger _logger;

    public NodeCreator(
        CustomNodeManager nodeManager,
        OpcUaServerConfiguration configuration,
        OpcUaNodeFactory nodeFactory,
        OpcUaSubjectServerBackgroundService source,
        ConnectorReferenceCounter<NodeState> subjectRefCounter,
        ModelChangePublisher modelChangePublisher,
        ILogger logger)
    {
        _nodeManager = nodeManager;
        _configuration = configuration;
        _nodeMapper = configuration.NodeMapper;
        _nodeFactory = nodeFactory;
        _source = source;
        _subjectRefCounter = subjectRefCounter;
        _modelChangePublisher = modelChangePublisher;
        _logger = logger;
    }

    /// <summary>
    /// Gets the namespace index for the node manager.
    /// </summary>
    public ushort NamespaceIndex => _nodeManager.NamespaceIndexes[0];

    /// <summary>
    /// Creates nodes for all properties of a subject.
    /// </summary>
    public void CreateSubjectNodes(NodeId parentNodeId, RegisteredSubject subject, string parentPath)
    {
        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute)
                continue;

            var propertyName = property.ResolvePropertyName(_nodeMapper);
            if (propertyName is not null)
            {
                var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(property);

                if (property.IsSubjectCollection)
                {
                    CreateCollectionObjectNode(propertyName, property, property.Children, parentNodeId, parentPath, nodeConfiguration);
                }
                else if (property.IsSubjectDictionary)
                {
                    CreateDictionaryObjectNode(propertyName, property, property.Children, parentNodeId, parentPath, nodeConfiguration);
                }
                else if (property.IsSubjectReference)
                {
                    var referencedChild = property.Children.SingleOrDefault();
                    if (referencedChild.Subject is not null)
                    {
                        CreateSubjectReferenceNode(propertyName, property, referencedChild.Subject, referencedChild.Index, parentNodeId, parentPath, nodeConfiguration);
                    }
                }
                else
                {
                    CreateVariableNode(propertyName, property, parentNodeId, parentPath);
                }
            }
        }
    }

    /// <summary>
    /// Creates a node for a subject reference (single subject property).
    /// Handles both ObjectNode and VariableNode representations based on configuration.
    /// </summary>
    public void CreateSubjectReferenceNode(
        string propertyName,
        RegisteredSubjectProperty property,
        IInterceptorSubject subject,
        object? index,
        NodeId parentNodeId,
        string parentPath,
        OpcUaNodeConfiguration? nodeConfiguration)
    {
        if (nodeConfiguration?.NodeClass == OpcUaNodeClass.Variable)
        {
            CreateVariableNodeForSubject(propertyName, property, parentNodeId, parentPath);
        }
        else
        {
            var path = parentPath + propertyName;
            var browseName = _nodeFactory.GetBrowseName(_nodeManager, propertyName, nodeConfiguration, index);
            var referenceTypeId = _nodeFactory.GetReferenceTypeId(_nodeManager, nodeConfiguration);
            CreateChildObject(property, browseName, subject, path, parentNodeId, referenceTypeId);
        }
    }

    /// <summary>
    /// Gets an existing container node or creates a new folder node for collections/dictionaries.
    /// </summary>
    public NodeState GetOrCreateContainerNode(
        string propertyName,
        OpcUaNodeConfiguration? nodeConfiguration,
        NodeId parentNodeId,
        string parentPath)
    {
        var containerNodeId = _nodeFactory.GetNodeId(_nodeManager, nodeConfiguration, parentPath + propertyName);
        var existingNode = _nodeManager.FindNode(containerNodeId);

        if (existingNode is not null)
        {
            return existingNode;
        }

        // Container doesn't exist yet - create it
        var browseName = _nodeFactory.GetBrowseName(_nodeManager, propertyName, nodeConfiguration, null);
        var typeDefinitionId = _nodeFactory.GetTypeDefinitionId(_nodeManager, nodeConfiguration);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(_nodeManager, nodeConfiguration);

        return _nodeFactory.CreateFolderNode(_nodeManager, parentNodeId, containerNodeId, browseName, typeDefinitionId, referenceTypeId, nodeConfiguration);
    }

    /// <summary>
    /// Creates a child node for a collection item.
    /// </summary>
    public void CreateCollectionChildNode(
        RegisteredSubjectProperty property,
        IInterceptorSubject subject,
        object childIndex,
        string propertyName,
        string parentPath,
        NodeId containerNodeId,
        OpcUaNodeConfiguration? nodeConfiguration)
    {
        var childBrowseName = new QualifiedName($"{propertyName}[{childIndex}]", NamespaceIndex);
        var childPath = $"{parentPath}{propertyName}[{childIndex}]";
        var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(_nodeManager, nodeConfiguration);

        CreateChildObject(property, childBrowseName, subject, childPath, containerNodeId, childReferenceTypeId);
    }

    /// <summary>
    /// Creates a child node for a dictionary item.
    /// Returns false if the index is null or empty (invalid key).
    /// </summary>
    public bool CreateDictionaryChildNode(
        RegisteredSubjectProperty property,
        IInterceptorSubject subject,
        object? index,
        string propertyName,
        string parentPath,
        NodeId containerNodeId,
        OpcUaNodeConfiguration? nodeConfiguration)
    {
        var indexString = index?.ToString();
        if (string.IsNullOrEmpty(indexString))
        {
            _logger.LogWarning(
                "Dictionary property '{PropertyName}' has a child with null or empty key. Skipping OPC UA node creation.",
                propertyName);
            return false;
        }

        var childBrowseName = new QualifiedName(indexString, NamespaceIndex);
        var childPath = parentPath + propertyName + PathDelimiter + index;
        var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(_nodeManager, nodeConfiguration);

        CreateChildObject(property, childBrowseName, subject, childPath, containerNodeId, childReferenceTypeId);
        return true;
    }

    /// <summary>
    /// Creates nodes for a collection property and all its child nodes.
    /// In Flat mode, children are created directly under the parent.
    /// In Container mode, a folder node is created first, then children are added under it.
    /// </summary>
    public void CreateCollectionObjectNode(
        string propertyName,
        RegisteredSubjectProperty property,
        ICollection<SubjectPropertyChild> children,
        NodeId parentNodeId,
        string parentPath,
        OpcUaNodeConfiguration? nodeConfiguration)
    {
        // Determine collection structure mode (default is Container for backward compatibility)
        var collectionStructure = nodeConfiguration?.CollectionStructure ?? CollectionNodeStructure.Container;
        if (collectionStructure == CollectionNodeStructure.Flat)
        {
            // Flat mode: create children directly under the parent node
            foreach (var child in children)
            {
                CreateCollectionChildNode(property, child.Subject, child.Index!, propertyName, parentPath, parentNodeId, nodeConfiguration);
            }
        }
        else
        {
            // Container mode: create folder first, then children under it
            var containerNode = GetOrCreateContainerNode(propertyName, nodeConfiguration, parentNodeId, parentPath);

            foreach (var child in children)
            {
                CreateCollectionChildNode(property, child.Subject, child.Index!, propertyName, parentPath, containerNode.NodeId, nodeConfiguration);
            }
        }
    }

    /// <summary>
    /// Creates a folder node for a dictionary property and all its child nodes.
    /// </summary>
    public void CreateDictionaryObjectNode(
        string propertyName,
        RegisteredSubjectProperty property,
        ICollection<SubjectPropertyChild> children,
        NodeId parentNodeId,
        string parentPath,
        OpcUaNodeConfiguration? nodeConfiguration)
    {
        var containerNode = GetOrCreateContainerNode(propertyName, nodeConfiguration, parentNodeId, parentPath);

        foreach (var child in children)
        {
            CreateDictionaryChildNode(property, child.Subject, child.Index, propertyName, parentPath, containerNode.NodeId, nodeConfiguration);
        }
    }

    /// <summary>
    /// Creates a variable node for a property.
    /// </summary>
    public BaseDataVariableState CreateVariableNode(
        string propertyName,
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        string parentPath,
        RegisteredSubjectProperty? configurationProperty = null)
    {
        // Use configurationProperty for node identity, property for value/type
        var actualConfigurationProperty = configurationProperty ?? property;

        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(actualConfigurationProperty);
        var nodeId = _nodeFactory.GetNodeId(_nodeManager, nodeConfiguration, parentPath + propertyName);
        var browseName = _nodeFactory.GetBrowseName(_nodeManager, propertyName, nodeConfiguration, null);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(_nodeManager, nodeConfiguration);
        var dataTypeOverride = _nodeFactory.GetDataTypeOverride(_nodeManager, _nodeMapper.TryGetNodeConfiguration(property));

        var variableNode = ConfigureVariableNode(property, parentNodeId, nodeId, browseName, referenceTypeId, dataTypeOverride, nodeConfiguration);

        property.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, variableNode);

        CreateAttributeNodes(variableNode, property, parentPath + propertyName);
        return variableNode;
    }

    /// <summary>
    /// Creates attribute nodes for a property.
    /// </summary>
    public void CreateAttributeNodes(NodeState parentNode, RegisteredSubjectProperty property, string parentPath)
    {
        foreach (var attribute in property.Attributes)
        {
            var attributeConfiguration = _nodeMapper.TryGetNodeConfiguration(attribute);
            if (attributeConfiguration is null)
                continue;

            var attributeName = attributeConfiguration.BrowseName ?? attribute.BrowseName;
            var attributePath = parentPath + PathDelimiter + attributeName;
            var referenceTypeId = _nodeFactory.GetReferenceTypeId(_nodeManager, attributeConfiguration) ?? ReferenceTypeIds.HasProperty;

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

    /// <summary>
    /// Creates a variable node for an attribute.
    /// </summary>
    public BaseDataVariableState CreateVariableNodeForAttribute(
        string attributeName,
        RegisteredSubjectProperty attribute,
        NodeId parentNodeId,
        string path,
        NodeId referenceTypeId)
    {
        var nodeConfiguration = _nodeMapper.TryGetNodeConfiguration(attribute);
        var nodeId = _nodeFactory.GetNodeId(_nodeManager, nodeConfiguration, path);
        var browseName = _nodeFactory.GetBrowseName(_nodeManager, attributeName, nodeConfiguration, null);
        var dataTypeOverride = _nodeFactory.GetDataTypeOverride(_nodeManager, nodeConfiguration);

        var variableNode = ConfigureVariableNode(attribute, parentNodeId, nodeId, browseName, referenceTypeId, dataTypeOverride, nodeConfiguration);

        attribute.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, variableNode);

        return variableNode;
    }

    /// <summary>
    /// Shared helper that configures a variable node with value, access levels, array dimensions, and state change handler.
    /// </summary>
    public BaseDataVariableState ConfigureVariableNode(
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

        var variableNode = _nodeFactory.CreateVariableNode(_nodeManager, parentNodeId, nodeId, browseName, typeInfo, referenceTypeId, dataTypeOverride, nodeConfiguration);
        _nodeFactory.AddAdditionalReferences(_nodeManager, variableNode, nodeConfiguration);
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

    /// <summary>
    /// Creates a variable node for a subject when the subject should be represented as a VariableNode.
    /// </summary>
    public void CreateVariableNodeForSubject(string propertyName, RegisteredSubjectProperty property, NodeId parentNodeId, string parentPath)
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

    /// <summary>
    /// Creates a child object node for a subject.
    /// Uses reference counting - if the subject already exists, adds a reference instead of creating a new node.
    /// </summary>
    public void CreateChildObject(RegisteredSubjectProperty property, QualifiedName browseName,
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
            var nodeId = _nodeFactory.GetNodeId(_nodeManager, nodeConfiguration, path);
            var typeDefinitionId = GetTypeDefinitionIdForSubject(subject);

            var node = _nodeFactory.CreateObjectNode(_nodeManager, parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId, nodeConfiguration);
            _nodeFactory.AddAdditionalReferences(_nodeManager, node, nodeConfiguration);
            return node;
        }, out var nodeState);

        if (isFirst)
        {
            // First reference - recursively create child nodes
            CreateSubjectNodes(nodeState.NodeId, registeredSubject, path + PathDelimiter);

            // Queue model change event for node creation
            _modelChangePublisher.QueueChange(nodeState.NodeId, ModelChangeStructureVerbMask.NodeAdded);
        }
        else
        {
            // Subject already created, add reference from parent to existing node
            var parentNode = _nodeManager.FindNode(parentNodeId);
            parentNode.AddReference(referenceTypeId ?? ReferenceTypeIds.HasComponent, false, nodeState.NodeId);

            // Queue model change event for reference addition
            _modelChangePublisher.QueueChange(nodeState.NodeId, ModelChangeStructureVerbMask.ReferenceAdded);
        }
    }

    /// <summary>
    /// Gets the type definition ID for a subject based on its OpcUaNode attribute.
    /// </summary>
    public NodeId? GetTypeDefinitionIdForSubject(IInterceptorSubject subject)
    {
        // For subjects, check if type has OpcUaNode attribute at class level
        var typeAttribute = subject.GetType().GetCustomAttribute<OpcUaNodeAttribute>();
        return _nodeFactory.GetTypeDefinitionId(_nodeManager, typeAttribute);
    }
}
